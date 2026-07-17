using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lurp.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Lurp;

public sealed class IncrementalIndexer
{
    private readonly IIndexStore _store;
    private readonly string _gitRoot;
    private readonly string _solutionPath;
    private readonly string _outputDir;
    private readonly HashSet<string> _skipAdapters;
    private readonly string? _jsonExportPath;

    public IncrementalIndexer(
        IIndexStore store,
        string gitRoot,
        string solutionPath,
        string outputDir,
        HashSet<string> skipAdapters,
        string? jsonExportPath = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gitRoot = gitRoot ?? throw new ArgumentNullException(nameof(gitRoot));
        _solutionPath = solutionPath ?? throw new ArgumentNullException(nameof(solutionPath));
        _outputDir = outputDir ?? throw new ArgumentNullException(nameof(outputDir));
        _skipAdapters = skipAdapters;
        _jsonExportPath = jsonExportPath;
    }

    public sealed record DocumentChangeInfo(
        string RelativePath,
        DocumentChangeKind ChangeKind,
        string? OldDocumentVersionId = null);

    public enum DocumentChangeKind
    {
        Unchanged,
        Changed,
        New,
        Deleted
    }

    public sealed record IncrementalResult(
        string NewSnapshotId,
        string PreviousSnapshotId,
        int ChangedDocumentCount,
        int DeclarationsExtracted,
        int EdgesExtracted,
        int DiagnosticsExtracted)
    {
        public bool HasChanges => ChangedDocumentCount > 0 || DeclarationsExtracted > 0 || EdgesExtracted > 0;
    }

    public async Task<IncrementalResult> RunIncrementalAsync(
        Solution solution,
        WorkspaceInfo workspaceInfo,
        Storage.SnapshotManifest previousManifest)
    {
        var previousSnapshotId = previousManifest.SnapshotId;
        var previousRichManifest = SnapshotManifest.FromStorageManifest(previousManifest);

        Console.Write("Hashing documents and detecting changes... ");
        var docChanges = DetectChanges(workspaceInfo, previousRichManifest);
        var changedDocs = docChanges.Where(c => c.ChangeKind != DocumentChangeKind.Unchanged).ToList();
        var changedPaths = changedDocs.Select(c => c.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"done ({changedDocs.Count} changed, {docChanges.Count - changedDocs.Count} unchanged).");

        if (changedDocs.Count == 0)
        {
            Console.WriteLine("No changes detected. Skipping incremental index.");
            return new IncrementalResult(
                NewSnapshotId: previousSnapshotId,
                PreviousSnapshotId: previousSnapshotId,
                ChangedDocumentCount: 0,
                DeclarationsExtracted: 0,
                EdgesExtracted: 0,
                DiagnosticsExtracted: 0);
        }

        foreach (var change in changedDocs)
        {
            Console.WriteLine($"  {change.ChangeKind}: {change.RelativePath}");
        }

        Console.Write("Identifying affected projects... ");
        var affectedProjects = IdentifyAffectedProjects(solution, changedPaths);
        Console.WriteLine($"{affectedProjects.Count} affected: {string.Join(", ", affectedProjects)}");

        var oldDocVersionIds = _store.GetDocumentVersionIdsForDocuments(previousSnapshotId, changedPaths);
        var oldDocVersionIdSet = new HashSet<string>(oldDocVersionIds);

        Console.Write("Loading compilations for affected projects... ");
        var affectedCompilations = new Dictionary<string, Compilation>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            if (!affectedProjects.Contains(project.Name))
                continue;
            var compilation = await project.GetCompilationAsync();
            if (compilation != null)
                affectedCompilations[project.Name] = compilation;
        }
        Console.WriteLine($"done ({affectedCompilations.Count} compilations).");

        var snapshotId = SnapshotId.New();
        var newSnapshotIdStr = snapshotId.ToString();
        var newManifest = SnapshotManifest.FromWorkspace(workspaceInfo, snapshotId, SnapshotId.Parse(previousSnapshotId));

        Console.Write("Saving new snapshot manifest... ");
        newManifest.Save(_store, workspaceInfo.DocumentContents, _jsonExportPath);
        Console.WriteLine("done.");

        _store.MarkSnapshotInProgress(newSnapshotIdStr);

        int totalDeclarations = 0;
        int totalEdges = 0;
        int totalDiagnostics = 0;

        try
        {
            Console.Write("Preparing snapshot data (copy forward, remove stale)... ");

            _store.CopyEdgesToSnapshot(previousSnapshotId, newSnapshotIdStr);
            _store.CopySnapshotDiagnostics(previousSnapshotId, newSnapshotIdStr);

            // Delete ALL edges from affected-project documents before re-extraction.
            // We re-extract the full project compilation, so deleting only changedPaths
            // would leave unchanged files' edges duplicated.
            var affectedProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in solution.Projects)
            {
                if (!affectedProjects.Contains(project.Name))
                    continue;
                foreach (var doc in project.Documents)
                {
                    if (doc.FilePath == null) continue;
                    affectedProjectPaths.Add(GetRelativePath(doc.FilePath, _gitRoot));
                }
            }

            if (affectedProjectPaths.Count > 0)
                _store.DeleteEdgesByDocumentPaths(newSnapshotIdStr, affectedProjectPaths);

            // Also delete edges with NULL source_document_path — they can't be matched
            // by the IN clause above (NULL != anything in SQL), so they'd accumulate
            // as duplicates on every incremental pass. Re-extraction will regenerate
            // them. Scoped to the affected projects' assembly identities so untouched
            // projects' null-path edges (copied forward above) are left alone.
            var affectedAssemblyIdentities = affectedCompilations.Values
                .Select(c => c.Assembly.Identity.GetDisplayName())
                .ToList();
            _store.DeleteEdgesWithNullDocumentPathForAssemblies(newSnapshotIdStr, affectedAssemblyIdentities);

            if (oldDocVersionIdSet.Count > 0)
                _store.DeleteDeclarationsByDocumentVersionIds(oldDocVersionIdSet);

            _store.CopySnapshotSymbols(previousSnapshotId, newSnapshotIdStr);

            // Delete stale diagnostics copied forward for affected projects so
            // the re-extraction below doesn't duplicate them.
            _store.DeleteDiagnosticsByProjectNames(newSnapshotIdStr, affectedProjects);

            Console.WriteLine("done.");

            Console.WriteLine("Extracting replacement facts for affected projects...");

            foreach (var (projectName, compilation) in affectedCompilations)
            {
                Console.Write($"  [{projectName}] ");

                var result = CompilationFactExtractor.ExtractAll(
                    compilation, workspaceInfo, newSnapshotIdStr, projectName, _skipAdapters,
                    logWarning: msg => Console.Error.Write($"  WARNING: {msg} "),
                    logError: msg => Console.Error.Write($"  ERROR: {msg} "));

                _store.SaveDeclarations(newSnapshotIdStr, result.Declarations);
                totalDeclarations += result.Declarations.Count;

                _store.SaveEdges(newSnapshotIdStr, result.Edges);
                totalEdges += result.Edges.Count;

                _store.SaveDiagnostics(newSnapshotIdStr, result.Diagnostics);
                totalDiagnostics += result.Diagnostics.Count;

                Console.WriteLine($"{result.Declarations.Count} symbols, {result.Edges.Count} edges, {result.Diagnostics.Count} diagnostics.");
            }

            Console.Write("Updating cross-document edges... ");
            var crossDocEdgesProcessed = await UpdateCrossDocumentEdgesAsync(
                solution, workspaceInfo, newSnapshotIdStr, previousSnapshotId, changedPaths, affectedProjects);
            totalEdges += crossDocEdgesProcessed;
            Console.WriteLine($"done ({crossDocEdgesProcessed} cross-document edges processed).");

            Console.Write("Rebuilding FTS5 search index... ");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _store.BuildSearchIndex(newSnapshotIdStr);
            sw.Stop();
            Console.WriteLine($"done ({sw.ElapsedMilliseconds} ms).");

            Console.Write("Computing semantic diff from previous snapshot... ");
            try
            {
                var differ = new Workspace.SemanticDiffer(_store);
                var diffChanges = differ.ComputeDiff(previousSnapshotId, newSnapshotIdStr);
                _store.SaveSemanticChanges(previousSnapshotId, newSnapshotIdStr, diffChanges);
                Console.WriteLine($"done ({diffChanges.Count} changes).");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WARNING: Semantic diff failed: {ex.Message}");
            }

            _store.MarkSnapshotComplete(newSnapshotIdStr);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Incremental index failed, snapshot {newSnapshotIdStr} left in 'in_progress' state: {ex.Message}");
            throw;
        }

        Console.WriteLine();
        Console.WriteLine($"Incremental index complete for snapshot {newSnapshotIdStr}");
        Console.WriteLine($"  Previous snapshot: {previousSnapshotId}");
        Console.WriteLine($"  Changed documents: {changedDocs.Count}");
        Console.WriteLine($"  Declarations:      {totalDeclarations}");
        Console.WriteLine($"  Edges:             {totalEdges}");
        Console.WriteLine($"  Diagnostics:       {totalDiagnostics}");

        return new IncrementalResult(
            NewSnapshotId: newSnapshotIdStr,
            PreviousSnapshotId: previousSnapshotId,
            ChangedDocumentCount: changedDocs.Count,
            DeclarationsExtracted: totalDeclarations,
            EdgesExtracted: totalEdges,
            DiagnosticsExtracted: totalDiagnostics);
    }

    private List<DocumentChangeInfo> DetectChanges(
        WorkspaceInfo workspaceInfo,
        SnapshotManifest previousManifest)
    {
        var results = new List<DocumentChangeInfo>();
        var currentDocs = workspaceInfo.Documents;
        var previousDocs = previousManifest.DocumentVersions;
        var processed = new HashSet<DocumentId>();

        foreach (var (docId, currentHash) in currentDocs)
        {
            processed.Add(docId);

            if (!previousDocs.TryGetValue(docId, out var previousHash))
            {

                results.Add(new DocumentChangeInfo(docId.ToString(), DocumentChangeKind.New));
            }
            else if (currentHash != previousHash)
            {

                results.Add(new DocumentChangeInfo(docId.ToString(), DocumentChangeKind.Changed));
            }
            else
            {

                results.Add(new DocumentChangeInfo(docId.ToString(), DocumentChangeKind.Unchanged));
            }
        }

        foreach (var (docId, _) in previousDocs)
        {
            if (!processed.Contains(docId))
            {
                results.Add(new DocumentChangeInfo(docId.ToString(), DocumentChangeKind.Deleted));
            }
        }

        return results;
    }

    private HashSet<string> IdentifyAffectedProjects(
        Solution solution,
        HashSet<string> changedPaths)
    {
        var affected = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath == null) continue;
                var relPath = GetRelativePath(document.FilePath, _gitRoot);
                if (changedPaths.Contains(relPath))
                {
                    affected.Add(project.Name);
                    break;
                }
            }
        }

        return affected;
    }

    private async Task<int> UpdateCrossDocumentEdgesAsync(
        Solution solution,
        WorkspaceInfo workspaceInfo,
        string newSnapshotId,
        string previousSnapshotId,
        HashSet<string> changedPaths,
        HashSet<string> alreadyProcessedProjects)
    {

        var oldDocVersionIds = _store.GetDocumentVersionIdsForDocuments(previousSnapshotId, changedPaths);
        if (oldDocVersionIds.Count == 0)
            return 0;

        var changedSymbolIds = _store.GetSymbolIdsByDocumentVersionIds(previousSnapshotId, oldDocVersionIds);
        if (changedSymbolIds.Count == 0)
            return 0;

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

        if (affectedPaths.Count == 0)
            return 0;

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

        // Projects already fully re-extracted by the main incremental loop are already
        // up to date; reprocessing them here would duplicate every edge in that project.
        affectedProjectNames.ExceptWith(alreadyProcessedProjects);

        if (affectedProjectNames.Count == 0)
            return 0;

        // Extractors run over the whole project compilation, not just affectedPaths, so
        // every document belonging to an affected project must have its stale edges
        // (copied forward from the previous snapshot) removed first — otherwise documents
        // outside affectedPaths keep their old edges alongside the freshly extracted ones.
        var affectedProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
        {
            if (!affectedProjectNames.Contains(project.Name))
                continue;
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath == null) continue;
                affectedProjectPaths.Add(GetRelativePath(doc.FilePath, _gitRoot));
            }
        }

        _store.DeleteEdgesByDocumentPaths(newSnapshotId, affectedProjectPaths);

        var crossDocCompilations = new Dictionary<string, Compilation>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            if (!affectedProjectNames.Contains(project.Name))
                continue;
            var compilation = await project.GetCompilationAsync();
            if (compilation != null)
                crossDocCompilations[project.Name] = compilation;
        }

        // Scoped by assembly identity — see the comment on
        // DeleteEdgesWithNullDocumentPathForAssemblies for why path-based
        // deletion can't cover these edges.
        var crossDocAssemblyIdentities = crossDocCompilations.Values
            .Select(c => c.Assembly.Identity.GetDisplayName())
            .ToList();
        _store.DeleteEdgesWithNullDocumentPathForAssemblies(newSnapshotId, crossDocAssemblyIdentities);

        int totalEdges = 0;
        foreach (var project in solution.Projects)
        {
            if (!affectedProjectNames.Contains(project.Name))
                continue;

            if (!crossDocCompilations.TryGetValue(project.Name, out var compilation))
                continue;

            var result = CompilationFactExtractor.ExtractAll(
                compilation, workspaceInfo, newSnapshotId, project.Name, _skipAdapters,
                logWarning: msg => Console.Error.Write($"  WARNING: {msg} "),
                logError: msg => Console.Error.Write($"  ERROR: {msg} "));

            _store.SaveEdges(newSnapshotId, result.Edges);
            totalEdges += result.Edges.Count;

            Console.Write($"  [cross-doc {project.Name}] {result.Edges.Count} edges. ");
        }

        return totalEdges;
    }

    private void CopyForwardSymbols(
        string previousSnapshotId,
        string newSnapshotId,
        HashSet<string> changedPaths)
    {

        var oldDocVersionIds = _store.GetDocumentVersionIdsForDocuments(previousSnapshotId, changedPaths);
        var oldDocVersionIdSet = new HashSet<string>(oldDocVersionIds);

        var changedSymbolIds = _store.GetSymbolIdsByDocumentVersionIds(previousSnapshotId, oldDocVersionIdSet);
        var changedSymbolIdSet = new HashSet<string>(changedSymbolIds);

        _store.CopySnapshotSymbols(previousSnapshotId, newSnapshotId);

    }

    private static string GetRelativePath(string fullPath, string gitRoot)
    {
        var normalizedRoot = Path.GetFullPath(gitRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = normalizedRoot + Path.DirectorySeparatorChar;
        return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
    }
}
