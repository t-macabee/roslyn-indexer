using Lurp.Storage;
using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

internal sealed class CrossDocumentEdgeRefresher(IIndexStore store, string gitRoot, HashSet<string> skipAdapters)
{
    private readonly IIndexStore _store = store;
    private readonly string _gitRoot = gitRoot;
    private readonly HashSet<string> _skipAdapters = skipAdapters;

    internal async Task<int> RefreshAsync(Solution solution, WorkspaceInfo workspaceInfo, string newSnapshotId, string previousSnapshotId, HashSet<string> changedPaths, HashSet<string> alreadyProcessedProjects)
    {
        var affectedPaths = FindAffectedDocPaths(previousSnapshotId, changedPaths);
        if (affectedPaths.Count == 0)
            return 0;

        var affectedProjectNames = ResolveProjectNames(solution, affectedPaths, alreadyProcessedProjects);
        if (affectedProjectNames.Count == 0)
            return 0;

        return await ProcessCompilationsAsync(solution, workspaceInfo, newSnapshotId, affectedProjectNames);
    }

    private HashSet<string> FindAffectedDocPaths(string previousSnapshotId, HashSet<string> changedPaths)
    {
        var oldDocVersionIds = _store.GetDocumentVersionIdsForDocuments(previousSnapshotId, changedPaths);
        if (oldDocVersionIds.Count == 0)
            return [];

        var changedSymbolIds = _store.GetSymbolIdsByDocumentVersionIds(previousSnapshotId, oldDocVersionIds);
        if (changedSymbolIds.Count == 0)
            return [];

        var affectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbolId in changedSymbolIds)
        {
            var incomingEdges = _store.GetIncomingEdges(previousSnapshotId, symbolId);
            foreach (var edge in incomingEdges)
            {
                if (edge.SourceDocumentPath != null && !changedPaths.Contains(edge.SourceDocumentPath))
                    affectedPaths.Add(edge.SourceDocumentPath);
            }
        }
        return affectedPaths;
    }

    private HashSet<string> ResolveProjectNames(Solution solution, HashSet<string> affectedPaths, HashSet<string> alreadyProcessedProjects)
    {
        Console.WriteLine($"  ({affectedPaths.Count} documents need cross-document edge refresh)");

        var affectedProjectNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath == null) continue;
                var relPath = GetRelativePath(doc.FilePath, _gitRoot);
                if (affectedPaths.Contains(relPath))
                {
                    affectedProjectNames.Add(project.Name);
                    break;
                }
            }
        }

        affectedProjectNames.ExceptWith(alreadyProcessedProjects);
        return affectedProjectNames;
    }

    private async Task<int> ProcessCompilationsAsync(Solution solution, WorkspaceInfo workspaceInfo, string newSnapshotId, HashSet<string> affectedProjectNames)
    {
        var affectedProjectPaths = CollectProjectDocPaths(solution, affectedProjectNames);
        _store.DeleteEdgesByDocumentPaths(newSnapshotId, affectedProjectPaths);

        var crossDocCompilations = await LoadCompilationsAsync(solution, affectedProjectNames);
        var crossDocAssemblyIdentities = crossDocCompilations.Values
            .Select(c => c.Assembly.Identity.GetDisplayName()).ToList();
        _store.DeleteEdgesWithNullDocumentPathForAssemblies(newSnapshotId, crossDocAssemblyIdentities);

        int totalEdges = 0;
        foreach (var project in solution.Projects)
        {
            if (!affectedProjectNames.Contains(project.Name))
                continue;
            if (!crossDocCompilations.TryGetValue(project.Name, out var compilation))
                continue;

            var result = CompilationFactExtractor.ExtractAll(compilation, workspaceInfo, newSnapshotId, project.Name, _skipAdapters,
                logWarning: msg => Console.Error.Write($"  WARNING: {msg} "),
                logError: msg => Console.Error.Write($"  ERROR: {msg} "));
            _store.SaveEdges(newSnapshotId, result.Edges);
            totalEdges += result.Edges.Count;
            Console.Write($"  [cross-doc {project.Name}] {result.Edges.Count} edges. ");
        }
        return totalEdges;
    }

    private HashSet<string> CollectProjectDocPaths(Solution solution, HashSet<string> projectNames)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
        {
            if (!projectNames.Contains(project.Name))
                continue;
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath == null) continue;
                paths.Add(GetRelativePath(doc.FilePath, _gitRoot));
            }
        }
        return paths;
    }

    private static async Task<Dictionary<string, Compilation>> LoadCompilationsAsync(Solution solution, HashSet<string> projectNames)
    {
        var compilations = new Dictionary<string, Compilation>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            if (!projectNames.Contains(project.Name))
                continue;
            var compilation = await project.GetCompilationAsync();
            if (compilation != null)
                compilations[project.Name] = compilation;
        }
        return compilations;
    }

    private static string GetRelativePath(string fullPath, string gitRoot)
    {
        var normalizedRoot = Path.GetFullPath(gitRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = normalizedRoot + Path.DirectorySeparatorChar;
        return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
    }
}
