using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lurp.Adapters;
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

        Console.Write("Preparing snapshot data (copy forward, remove stale)... ");

        _store.CopyEdgesToSnapshot(previousSnapshotId, newSnapshotIdStr);

        if (changedPaths.Count > 0)
            _store.DeleteEdgesByDocumentPaths(newSnapshotIdStr, changedPaths);

        if (oldDocVersionIdSet.Count > 0)
            _store.DeleteDeclarationsByDocumentVersionIds(oldDocVersionIdSet);

        _store.CopySnapshotSymbols(previousSnapshotId, newSnapshotIdStr);

        Console.WriteLine("done.");

        Console.WriteLine("Extracting replacement facts for affected projects...");
        int totalDeclarations = 0;
        int totalEdges = 0;
        int totalDiagnostics = 0;

        foreach (var (projectName, compilation) in affectedCompilations)
        {
            Console.Write($"  [{projectName}] ");

            var extractor = new SymbolExtractor(
                compilation,
                workspaceInfo.DocumentContents,
                workspaceInfo.Documents,
                workspaceInfo.GeneratedDocuments,
                newSnapshotIdStr);
            var declarations = extractor.ExtractAll();
            _store.SaveDeclarations(newSnapshotIdStr, declarations);
            totalDeclarations += declarations.Count;

            var typeEdges = extractor.ExtractEdges();
            _store.SaveEdges(newSnapshotIdStr, typeEdges);
            totalEdges += typeEdges.Count;

            var memberEdgeExtractor = new MemberEdgeExtractor(
                compilation, workspaceInfo.Documents, workspaceInfo.GeneratedDocuments, newSnapshotIdStr);
            var memberEdges = memberEdgeExtractor.ExtractAll();
            _store.SaveEdges(newSnapshotIdStr, memberEdges);
            totalEdges += memberEdges.Count;

            var polyExtractor = new PolymorphismExtractor(compilation, newSnapshotIdStr);
            var polyEdges = polyExtractor.ExtractAll();
            _store.SaveEdges(newSnapshotIdStr, polyEdges);
            totalEdges += polyEdges.Count;

            try
            {
                var reflectionExtractor = new ReflectionExtractor(compilation, newSnapshotIdStr);
                var reflectionEdges = reflectionExtractor.Extract();
                _store.SaveEdges(newSnapshotIdStr, reflectionEdges);
                totalEdges += reflectionEdges.Count;
                Console.Write($"  Reflection: {reflectionEdges.Count} edges. ");
            }
            catch (Exception ex)
            {
                Console.Error.Write($"  WARNING: Reflection extraction failed: {ex.Message} ");
            }

            int adapterEdgesCount = 0;
            var adaptersToRun = Adapters.AdapterRegistry.GetAdapters(_skipAdapters);
            foreach (var adapter in adaptersToRun)
            {
                try
                {
                    Console.Write($"  Adapter [{adapter.Name}]... ");
                    var adapterEdges = adapter.Extract(compilation, newSnapshotIdStr);
                    _store.SaveEdges(newSnapshotIdStr, adapterEdges);
                    adapterEdgesCount += adapterEdges.Count;
                    Console.Write($"{adapterEdges.Count} edges. ");
                }
                catch (Exception ex)
                {
                    Console.Error.Write($"  ERROR: Adapter '{adapter.Name}' failed: {ex.Message} ");
                }
            }
            totalEdges += adapterEdgesCount;

            var diagnostics = CompilationHelper.GetDiagnostics(projectName, compilation);
            _store.SaveDiagnostics(newSnapshotIdStr, diagnostics);
            totalDiagnostics += diagnostics.Count;

            Console.WriteLine($"{declarations.Count} symbols, {typeEdges.Count + memberEdges.Count + polyEdges.Count + adapterEdgesCount} edges, {diagnostics.Count} diagnostics.");
        }

        Console.Write("Updating cross-document edges... ");
        var crossDocEdgesProcessed = await UpdateCrossDocumentEdgesAsync(
            solution, workspaceInfo, newSnapshotIdStr, previousSnapshotId, changedPaths);
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
        HashSet<string> changedPaths)
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

        _store.DeleteEdgesByDocumentPaths(newSnapshotId, affectedPaths);

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

        int totalEdges = 0;
        foreach (var project in solution.Projects)
        {
            if (!affectedProjectNames.Contains(project.Name))
                continue;

            var compilation = await project.GetCompilationAsync();
            if (compilation == null)
                continue;

            var typeExtractor = new SymbolExtractor(
                compilation,
                workspaceInfo.DocumentContents,
                workspaceInfo.Documents,
                workspaceInfo.GeneratedDocuments,
                newSnapshotId);
            var typeEdges = typeExtractor.ExtractEdges();
            _store.SaveEdges(newSnapshotId, typeEdges);
            totalEdges += typeEdges.Count;

            var memberEdgeExtractor = new MemberEdgeExtractor(
                compilation, workspaceInfo.Documents, workspaceInfo.GeneratedDocuments, newSnapshotId);
            var memberEdges = memberEdgeExtractor.ExtractAll();
            _store.SaveEdges(newSnapshotId, memberEdges);
            totalEdges += memberEdges.Count;

            var polyExtractor = new PolymorphismExtractor(compilation, newSnapshotId);
            var polyEdges = polyExtractor.ExtractAll();
            _store.SaveEdges(newSnapshotId, polyEdges);
            totalEdges += polyEdges.Count;

            try
            {
                var reflectionExtractor = new ReflectionExtractor(compilation, newSnapshotId);
                var reflectionEdges = reflectionExtractor.Extract();
                _store.SaveEdges(newSnapshotId, reflectionEdges);
                totalEdges += reflectionEdges.Count;
            }
            catch { }

            var adaptersToRun = Adapters.AdapterRegistry.GetAdapters(_skipAdapters);
            foreach (var adapter in adaptersToRun)
            {
                try
                {
                    var adapterEdges = adapter.Extract(compilation, newSnapshotId);
                    _store.SaveEdges(newSnapshotId, adapterEdges);
                    totalEdges += adapterEdges.Count;
                }
                catch { }
            }

            Console.Write($"  [cross-doc {project.Name}] {typeEdges.Count + memberEdges.Count + polyEdges.Count} edges. ");
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
