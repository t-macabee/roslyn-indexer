using Lurp.Storage;
using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

public sealed class IncrementalIndexer(IIndexStore store, string gitRoot, string solutionPath, string outputDir, HashSet<string> skipAdapters, string? jsonExportPath = null)
{
    private readonly IIndexStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly string _gitRoot = gitRoot ?? throw new ArgumentNullException(nameof(gitRoot));
    private readonly string _solutionPath = solutionPath ?? throw new ArgumentNullException(nameof(solutionPath));
    private readonly string _outputDir = outputDir ?? throw new ArgumentNullException(nameof(outputDir));
    private readonly HashSet<string> _skipAdapters = skipAdapters;
    private readonly string? _jsonExportPath = jsonExportPath;

    private readonly DocumentChangeDetector _changeDetector = new(gitRoot);

    public sealed record IncrementalResult(string NewSnapshotId, string PreviousSnapshotId, int ChangedDocumentCount, int DeclarationsExtracted, int EdgesExtracted, int DiagnosticsExtracted)
    {
        public bool HasChanges => ChangedDocumentCount > 0 || DeclarationsExtracted > 0 || EdgesExtracted > 0;
    }

    // Incremental indexing strategy:
    //   1. Hash all documents and compare against the previous manifest to find changed/new/deleted files.
    //   2. Identify which projects are affected by the changes.
    //   3. Load compilations only for affected projects (skip unchanged ones).
    //   4. Create a new snapshot manifest, copy forward edges/diagnostics/symbols from the previous snapshot.
    //   5. Remove stale data (edges for changed documents, declarations by old version ids, diagnostics).
    //   6. Re-extract declarations, edges, and diagnostics from affected compilations.
    //   7. Refresh cross-document edges for documents that reference changed symbols.
    //   8. Rebuild the FTS5 search index and compute a semantic diff against the previous snapshot.
    public async Task<IncrementalResult> RunIncrementalAsync(Solution solution, WorkspaceInfo workspaceInfo, Storage.SnapshotRow previousManifest)
    {
        var previousSnapshotId = previousManifest.SnapshotId;
        var previousRichManifest = SnapshotManifest.FromStorageManifest(previousManifest);
        var timings = new List<SnapshotTimingRow>();

        // Step 1: Change Detection
        var sw1 = System.Diagnostics.Stopwatch.StartNew();
        var (changedDocs, changedPaths) = _changeDetector.DetectAndLogChanges(workspaceInfo, previousRichManifest);
        sw1.Stop();
        timings.Add(new SnapshotTimingRow("change_detection", sw1.ElapsedMilliseconds, DateTime.UtcNow));

        if (changedDocs.Count == 0)
            return new IncrementalResult(NewSnapshotId: previousSnapshotId, PreviousSnapshotId: previousSnapshotId, ChangedDocumentCount: 0, DeclarationsExtracted: 0, EdgesExtracted: 0, DiagnosticsExtracted: 0);

        // Step 2: Affected Project Resolution
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        Console.Write("Identifying affected projects... ");
        var affectedProjects = _changeDetector.IdentifyAffectedProjects(solution, changedPaths);
        Console.WriteLine($"{affectedProjects.Count} affected: {string.Join(", ", affectedProjects)}");

        var oldDocVersionIds = _store.GetDocumentVersionIdsForDocuments(previousSnapshotId, changedPaths);
        var oldDocVersionIdSet = new HashSet<string>(oldDocVersionIds);
        sw2.Stop();
        timings.Add(new SnapshotTimingRow("affected_project_resolution", sw2.ElapsedMilliseconds, DateTime.UtcNow));

        // Step 3: Compilation Load
        var sw3 = System.Diagnostics.Stopwatch.StartNew();
        var affectedCompilations = await LoadAffectedCompilationsAsync(solution, affectedProjects);
        sw3.Stop();
        timings.Add(new SnapshotTimingRow("compilation_load", sw3.ElapsedMilliseconds, DateTime.UtcNow));

        var snapshotId = SnapshotId.New();
        var newSnapshotIdStr = snapshotId.ToString();
        var newManifest = SnapshotManifest.FromWorkspace(workspaceInfo, snapshotId, SnapshotId.Parse(previousSnapshotId));

        // Step 4: Manifest Creation
        var sw4 = System.Diagnostics.Stopwatch.StartNew();
        Console.Write("Saving new snapshot manifest... ");
        newManifest.Save(_store, workspaceInfo.DocumentContents, _jsonExportPath);
        Console.WriteLine("done.");
        sw4.Stop();
        timings.Add(new SnapshotTimingRow("manifest_creation", sw4.ElapsedMilliseconds, DateTime.UtcNow));

        // Populate extractor registry (idempotent — no-op on re-runs)
        _store.UpsertExtractors(ExtractorRegistry.All);

        // Check for stale extractor versions in the previous snapshot before
        // copy-forward.  If any edge carries an extractor_version not present
        // in the current registry, the copied-forward data would be stale and
        // a full rebuild is required.
        if (_store.HasStaleExtractorVersions(previousSnapshotId))
        {
            _store.DeleteSnapshotData(newSnapshotIdStr);
            throw new InvalidOperationException(
                "Extractor version staleness detected — some edges in the previous snapshot " +
                "reference extractor versions not in the current registry. Full rebuild required.");
        }

        int totalDeclarations = 0;
        int totalEdges = 0;
        int totalDiagnostics = 0;

        try
        {
            // Step 5: Stale-Data Removal
            var sw5 = System.Diagnostics.Stopwatch.StartNew();
            PrepareSnapshotData(solution, previousSnapshotId, newSnapshotIdStr, affectedProjects, oldDocVersionIdSet, changedPaths);
            sw5.Stop();
            timings.Add(new SnapshotTimingRow("stale_data_removal", sw5.ElapsedMilliseconds, DateTime.UtcNow));

            // Step 6: Re-extraction
            var sw6 = System.Diagnostics.Stopwatch.StartNew();
            (totalDeclarations, totalEdges, totalDiagnostics) =
                ExtractReplacementFacts(workspaceInfo, newSnapshotIdStr, affectedCompilations, changedPaths);
            sw6.Stop();
            timings.Add(new SnapshotTimingRow("re_extraction", sw6.ElapsedMilliseconds, DateTime.UtcNow));

            // Step 6b: Prune symbols that were in changed documents' old versions
            // but are no longer present after re-extraction
            var prunedSymbolIds = PruneRemovedSymbols(previousSnapshotId, newSnapshotIdStr, oldDocVersionIdSet, changedPaths);

            // Compute the set of symbol IDs that need their FTS entries refreshed:
            // all symbols currently declared in the changed documents after re-extraction,
            // plus any symbols that were pruned (their stale FTS rows must be deleted).
            var changedSymbolIds = ComputeChangedSymbolIds(newSnapshotIdStr, changedPaths);
            foreach (var id in prunedSymbolIds)
                changedSymbolIds.Add(id);

            // Step 7: Cross-doc Edge Refresh + Step 8: FTS Rebuild + Diff (in FinalizeSnapshotAsync)
            totalEdges += await FinalizeSnapshotAsync(solution, workspaceInfo, newSnapshotIdStr, previousSnapshotId, changedPaths, changedSymbolIds, affectedProjects, timings);
        }
        catch (Exception ex)
        {
            // Compensating delete: clean up partial snapshot data on failure
            // so no orphaned in_progress snapshot remains.
            try { _store.DeleteSnapshotData(newSnapshotIdStr); }
            catch { /* best-effort cleanup */ }
            Console.Error.WriteLine($"ERROR: Incremental index failed mid-operation, snapshot {newSnapshotIdStr} cleaned up: {ex.Message}");
            throw;
        }

        // Persist all timings
        try { _store.SaveTimings(newSnapshotIdStr, timings); }
        catch (Exception ex) { Console.Error.WriteLine($"WARNING: Failed to save timings: {ex.Message}"); }

        Console.WriteLine();
        Console.WriteLine($"Incremental index complete for snapshot {newSnapshotIdStr}");
        Console.WriteLine($"  Previous snapshot: {previousSnapshotId}");
        Console.WriteLine($"  Changed documents: {changedDocs.Count}");
        Console.WriteLine($"  Declarations:      {totalDeclarations}");
        Console.WriteLine($"  Edges:             {totalEdges}");
        Console.WriteLine($"  Diagnostics:       {totalDiagnostics}");

        return new IncrementalResult(NewSnapshotId: newSnapshotIdStr, PreviousSnapshotId: previousSnapshotId, ChangedDocumentCount: changedDocs.Count, DeclarationsExtracted: totalDeclarations, EdgesExtracted: totalEdges, DiagnosticsExtracted: totalDiagnostics);
    }

    private async Task<Dictionary<string, Compilation>> LoadAffectedCompilationsAsync(Solution solution, HashSet<string> affectedProjects)
    {
        Console.Write("Loading compilations for affected projects... ");
        var result = new Dictionary<string, Compilation>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            if (!affectedProjects.Contains(project.Name))
                continue;
            var compilation = await project.GetCompilationAsync();
            if (compilation != null)
                result[project.Name] = compilation;
        }
        Console.WriteLine($"done ({result.Count} compilations).");
        return result;
    }

    private void PrepareSnapshotData(Solution solution, string previousSnapshotId, string newSnapshotIdStr, HashSet<string> affectedProjects, HashSet<string> oldDocVersionIdSet, HashSet<string> changedPaths)
    {
        Console.Write("Preparing snapshot data (copy forward, remove stale)... ");

        _store.CopyEdgesToSnapshot(previousSnapshotId, newSnapshotIdStr);
        _store.CopySnapshotDiagnostics(previousSnapshotId, newSnapshotIdStr);
        _store.CopyAnnotationsToSnapshot(previousSnapshotId, newSnapshotIdStr);

        // Only delete edges for the changed documents, not the entire affected project.
        // We now scope re-extraction to changed documents only, so unchanged documents
        // within affected projects keep their copied-forward edges intact.
        if (changedPaths.Count > 0)
            _store.DeleteEdgesByDocumentPaths(newSnapshotIdStr, changedPaths);

        // Null-path edges (from symbols with no DeclaringSyntaxReferences, e.g. an
        // implicit default constructor) can't be scoped to a document by path, so we
        // scope the delete to symbols declared in the documents that actually changed
        // rather than the whole affected assembly — re-extraction is scoped the same
        // way, so an unchanged document elsewhere in the assembly must keep its
        // copied-forward null-path edges intact.
        if (oldDocVersionIdSet.Count > 0)
        {
            var changedSymbolIds = _store.GetSymbolIdsByDocumentVersionIds(previousSnapshotId, oldDocVersionIdSet);
            _store.DeleteEdgesWithNullDocumentPathForSymbols(newSnapshotIdStr, changedSymbolIds);
        }

        if (oldDocVersionIdSet.Count > 0)
            _store.DeleteDeclarationsByDocumentVersionIds(oldDocVersionIdSet);

        _store.CopySnapshotSymbols(previousSnapshotId, newSnapshotIdStr);
        _store.CopySearchIndexToSnapshot(previousSnapshotId, newSnapshotIdStr);
        _store.DeleteDiagnosticsByProjectNames(newSnapshotIdStr, affectedProjects);

        Console.WriteLine("done.");
    }

    private (int Declarations, int Edges, int Diagnostics) ExtractReplacementFacts(
        WorkspaceInfo workspaceInfo, string newSnapshotIdStr, Dictionary<string, Compilation> affectedCompilations, HashSet<string> changedPaths)
    {
        Console.WriteLine("Extracting replacement facts for affected projects...");
        int totalDecl = 0, totalEdge = 0, totalDiag = 0;

        foreach (var (projectName, compilation) in affectedCompilations)
        {
            // Compute per-project scope: changed paths that belong to this project's compilation
            HashSet<string>? scopeDocs = null;
            HashSet<string>? scopeRelPaths = null; // relative-path version for adapter-edge filtering
            if (changedPaths.Count > 0)
            {
                scopeDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                scopeRelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var syntaxTree in compilation.SyntaxTrees)
                {
                    var filePath = syntaxTree.FilePath;
                    if (string.IsNullOrEmpty(filePath))
                        continue;
                    var relPath = DocumentChangeDetector.GetRelativePath(filePath, _gitRoot);
                    if (changedPaths.Contains(relPath))
                    {
                        scopeDocs.Add(filePath.Replace('\\', '/'));
                        scopeRelPaths.Add(relPath);
                    }
                }
                if (scopeDocs.Count == 0)
                {
                    scopeDocs = null;
                    scopeRelPaths = null;
                }
            }

            Console.Write($"  [{projectName}] ");
            var result = CompilationFactExtractor.ExtractAll(compilation, workspaceInfo, newSnapshotIdStr, projectName, _skipAdapters,
                logWarning: msg => Console.Error.Write($"  WARNING: {msg} "),
                logError: msg => Console.Error.Write($"  ERROR: {msg} "),
                scopeDocuments: scopeDocs);

            // Filter out edges anchored in unchanged documents within this project.
            // Those edges were already copied forward from the previous snapshot
            // and must not be written a second time.
            // Null-path edges (e.g. implicit constructors) cannot be scoped to a
            // document, so they pass through unfiltered.
            if (scopeRelPaths != null)
            {
                result.Edges.RemoveAll(e =>
                    e.SourceDocumentPath != null && !scopeRelPaths.Contains(e.SourceDocumentPath));
            }

            _store.SaveDeclarations(newSnapshotIdStr, result.Declarations);
            totalDecl += result.Declarations.Count;
            _store.SaveEdges(newSnapshotIdStr, result.Edges);
            totalEdge += result.Edges.Count;
            _store.SaveDiagnostics(newSnapshotIdStr, result.Diagnostics);
            totalDiag += result.Diagnostics.Count;

            Console.WriteLine($"{result.Declarations.Count} symbols, {result.Edges.Count} edges, {result.Diagnostics.Count} diagnostics.");
        }

        return (totalDecl, totalEdge, totalDiag);
    }

    private List<string> PruneRemovedSymbols(string previousSnapshotId, string newSnapshotIdStr, HashSet<string> oldDocVersionIdSet, HashSet<string> changedPaths)
    {
        if (oldDocVersionIdSet.Count == 0)
            return [];

        // Get symbols that were in the old document versions
        var oldSymbolIds = _store.GetSymbolIdsByDocumentVersionIds(previousSnapshotId, oldDocVersionIdSet);
        if (oldSymbolIds.Count == 0)
            return [];

        // After re-extraction, look up new document version IDs for the changed paths
        var pathToNewVersion = _store.GetDocumentVersionIdsByPath(newSnapshotIdStr);
        var newDocVersionIdSet = new HashSet<string>(
            changedPaths
                .Where(p => pathToNewVersion.ContainsKey(p))
                .Select(p => pathToNewVersion[p]));

        if (newDocVersionIdSet.Count == 0)
            return [];

        // Get symbols that are in the new document versions
        var newSymbolIds = new HashSet<string>(
            _store.GetSymbolIdsByDocumentVersionIds(newSnapshotIdStr, newDocVersionIdSet));

        // Prune symbols that were in old but not in new
        var removedSymbolIds = oldSymbolIds.Where(id => !newSymbolIds.Contains(id)).ToList();
        if (removedSymbolIds.Count > 0)
        {
            Console.Write($"Pruning {removedSymbolIds.Count} removed symbols... ");
            _store.DeleteSnapshotSymbolsBySymbolIds(newSnapshotIdStr, removedSymbolIds);
            Console.WriteLine("done.");
            return removedSymbolIds;
        }

        return [];
    }

    private async Task<int> FinalizeSnapshotAsync(Solution solution, WorkspaceInfo workspaceInfo, string newSnapshotIdStr, string previousSnapshotId, HashSet<string> changedPaths, HashSet<string> changedSymbolIds, HashSet<string> affectedProjects, List<SnapshotTimingRow> timings)
    {
        // Step 7: Cross-doc Edge Refresh
        var sw7 = System.Diagnostics.Stopwatch.StartNew();
        Console.Write("Updating cross-document edges... ");
        var refresher = new CrossDocumentEdgeRefresher(_store, _gitRoot, _skipAdapters);
        var crossDocEdgesProcessed = await refresher.RefreshAsync(solution, workspaceInfo, newSnapshotIdStr, previousSnapshotId, changedPaths, affectedProjects);
        Console.WriteLine($"done ({crossDocEdgesProcessed} cross-document edges processed).");
        sw7.Stop();
        timings.Add(new SnapshotTimingRow("cross_doc_edge_refresh", sw7.ElapsedMilliseconds, DateTime.UtcNow));

        // Step 7b: Remove edges targeting symbols not declared in this snapshot
        _store.DeleteOrphanEdges(newSnapshotIdStr);

        // Step 8: FTS Rebuild (incremental) + Diff
        var sw8 = System.Diagnostics.Stopwatch.StartNew();
        Console.Write("Rebuilding FTS5 search index (incremental)... ");
        _store.BuildSearchIndex(newSnapshotIdStr, changedPaths, changedSymbolIds);
        Console.WriteLine("done.");

        Console.Write("Computing semantic diff from previous snapshot... ");
        try
        {
            var differ = new SemanticDiffer(_store, _store, _store);
            var (diffChanges, skippedComparisons) = differ.ComputeDiff(previousSnapshotId, newSnapshotIdStr, changedPaths, changedSymbolIds);
            _store.SaveSemanticChanges(previousSnapshotId, newSnapshotIdStr, diffChanges);
            Console.WriteLine($"done ({diffChanges.Count} changes, {skippedComparisons} comparisons skipped).");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARNING: Semantic diff failed: {ex.Message}");
        }
        sw8.Stop();
        timings.Add(new SnapshotTimingRow("fts_rebuild_and_diff", sw8.ElapsedMilliseconds, DateTime.UtcNow));

        _store.MarkSnapshotComplete(newSnapshotIdStr);
        return crossDocEdgesProcessed;
    }

    private HashSet<string> ComputeChangedSymbolIds(string snapshotId, HashSet<string> changedPaths)
    {
        if (changedPaths.Count == 0)
            return new HashSet<string>();

        var pathToVersion = _store.GetDocumentVersionIdsByPath(snapshotId);
        var versionIds = new HashSet<string>(
            changedPaths
                .Where(p => pathToVersion.ContainsKey(p))
                .Select(p => pathToVersion[p]));

        if (versionIds.Count == 0)
            return new HashSet<string>();

        return new HashSet<string>(_store.GetSymbolIdsByDocumentVersionIds(snapshotId, versionIds));
    }

}
