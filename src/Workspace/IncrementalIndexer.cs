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

    private sealed record DocumentChangeInfo(string RelativePath, DocumentChangeKind ChangeKind, string? OldDocumentVersionId = null);

    private enum DocumentChangeKind
    {
        Unchanged,
        Changed,
        New,
        Deleted
    }

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
        var (changedDocs, changedPaths) = DetectAndLogChanges(workspaceInfo, previousRichManifest);
        sw1.Stop();
        timings.Add(new SnapshotTimingRow("change_detection", sw1.ElapsedMilliseconds, DateTime.UtcNow));

        if (changedDocs.Count == 0)
            return new IncrementalResult(NewSnapshotId: previousSnapshotId, PreviousSnapshotId: previousSnapshotId, ChangedDocumentCount: 0, DeclarationsExtracted: 0, EdgesExtracted: 0, DiagnosticsExtracted: 0);

        // Step 2: Affected Project Resolution
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        Console.Write("Identifying affected projects... ");
        var affectedProjects = IdentifyAffectedProjects(solution, changedPaths);
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
        newManifest.Save(_store, _store, workspaceInfo.DocumentContents, _jsonExportPath);
        Console.WriteLine("done.");
        sw4.Stop();
        timings.Add(new SnapshotTimingRow("manifest_creation", sw4.ElapsedMilliseconds, DateTime.UtcNow));

        _store.MarkSnapshotInProgress(newSnapshotIdStr);

        int totalDeclarations = 0;
        int totalEdges = 0;
        int totalDiagnostics = 0;

        try
        {
            // Step 5: Stale-Data Removal
            var sw5 = System.Diagnostics.Stopwatch.StartNew();
            PrepareSnapshotData(solution, previousSnapshotId, newSnapshotIdStr, affectedProjects, affectedCompilations, oldDocVersionIdSet);
            sw5.Stop();
            timings.Add(new SnapshotTimingRow("stale_data_removal", sw5.ElapsedMilliseconds, DateTime.UtcNow));

            // Step 6: Re-extraction
            var sw6 = System.Diagnostics.Stopwatch.StartNew();
            (totalDeclarations, totalEdges, totalDiagnostics) =
                ExtractReplacementFacts(workspaceInfo, newSnapshotIdStr, affectedCompilations);
            sw6.Stop();
            timings.Add(new SnapshotTimingRow("re_extraction", sw6.ElapsedMilliseconds, DateTime.UtcNow));

            // Step 7: Cross-doc Edge Refresh + Step 8: FTS Rebuild + Diff (in FinalizeSnapshotAsync)
            totalEdges += await FinalizeSnapshotAsync(solution, workspaceInfo, newSnapshotIdStr, previousSnapshotId, changedPaths, affectedProjects, timings);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Incremental index failed, snapshot {newSnapshotIdStr} left in 'in_progress' state: {ex.Message}");
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

    private (List<DocumentChangeInfo> ChangedDocs, HashSet<string> ChangedPaths) DetectAndLogChanges(WorkspaceInfo workspaceInfo, SnapshotManifest previousRichManifest)
    {
        Console.Write("Hashing documents and detecting changes... ");
        var docChanges = DetectChanges(workspaceInfo, previousRichManifest);
        var changedDocs = docChanges.Where(c => c.ChangeKind != DocumentChangeKind.Unchanged).ToList();
        var changedPaths = changedDocs.Select(c => c.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"done ({changedDocs.Count} changed, {docChanges.Count - changedDocs.Count} unchanged).");

        if (changedDocs.Count == 0)
        {
            Console.WriteLine("No changes detected. Skipping incremental index.");
        }
        else
        {
            foreach (var change in changedDocs)
                Console.WriteLine($"  {change.ChangeKind}: {change.RelativePath}");
        }

        return (changedDocs, changedPaths);
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

    private void PrepareSnapshotData(Solution solution, string previousSnapshotId, string newSnapshotIdStr, HashSet<string> affectedProjects, Dictionary<string, Compilation> affectedCompilations, HashSet<string> oldDocVersionIdSet)
    {
        Console.Write("Preparing snapshot data (copy forward, remove stale)... ");

        _store.CopyEdgesToSnapshot(previousSnapshotId, newSnapshotIdStr);
        _store.CopySnapshotDiagnostics(previousSnapshotId, newSnapshotIdStr);

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

        var affectedAssemblyIdentities = affectedCompilations.Values
            .Select(c => c.Assembly.Identity.GetDisplayName()).ToList();
        _store.DeleteEdgesWithNullDocumentPathForAssemblies(newSnapshotIdStr, affectedAssemblyIdentities);

        if (oldDocVersionIdSet.Count > 0)
            _store.DeleteDeclarationsByDocumentVersionIds(oldDocVersionIdSet);

        _store.CopySnapshotSymbols(previousSnapshotId, newSnapshotIdStr);
        _store.DeleteDiagnosticsByProjectNames(newSnapshotIdStr, affectedProjects);

        Console.WriteLine("done.");
    }

    private (int Declarations, int Edges, int Diagnostics) ExtractReplacementFacts(
        WorkspaceInfo workspaceInfo, string newSnapshotIdStr, Dictionary<string, Compilation> affectedCompilations)
    {
        Console.WriteLine("Extracting replacement facts for affected projects...");
        int totalDecl = 0, totalEdge = 0, totalDiag = 0;

        foreach (var (projectName, compilation) in affectedCompilations)
        {
            Console.Write($"  [{projectName}] ");
            var result = CompilationFactExtractor.ExtractAll(compilation, workspaceInfo, newSnapshotIdStr, projectName, _skipAdapters,
                logWarning: msg => Console.Error.Write($"  WARNING: {msg} "),
                logError: msg => Console.Error.Write($"  ERROR: {msg} "));

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

    private async Task<int> FinalizeSnapshotAsync(Solution solution, WorkspaceInfo workspaceInfo, string newSnapshotIdStr, string previousSnapshotId, HashSet<string> changedPaths, HashSet<string> affectedProjects, List<SnapshotTimingRow> timings)
    {
        // Step 7: Cross-doc Edge Refresh
        var sw7 = System.Diagnostics.Stopwatch.StartNew();
        Console.Write("Updating cross-document edges... ");
        var refresher = new CrossDocumentEdgeRefresher(_store, _gitRoot, _skipAdapters);
        var crossDocEdgesProcessed = await refresher.RefreshAsync(solution, workspaceInfo, newSnapshotIdStr, previousSnapshotId, changedPaths, affectedProjects);
        Console.WriteLine($"done ({crossDocEdgesProcessed} cross-document edges processed).");
        sw7.Stop();
        timings.Add(new SnapshotTimingRow("cross_doc_edge_refresh", sw7.ElapsedMilliseconds, DateTime.UtcNow));

        // Step 8: FTS Rebuild + Diff
        var sw8 = System.Diagnostics.Stopwatch.StartNew();
        Console.Write("Rebuilding FTS5 search index... ");
        _store.BuildSearchIndex(newSnapshotIdStr);
        Console.WriteLine("done.");

        Console.Write("Computing semantic diff from previous snapshot... ");
        try
        {
            var differ = new SemanticDiffer(_store, _store, _store);
            var diffChanges = differ.ComputeDiff(previousSnapshotId, newSnapshotIdStr);
            _store.SaveSemanticChanges(previousSnapshotId, newSnapshotIdStr, diffChanges);
            Console.WriteLine($"done ({diffChanges.Count} changes).");
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

    private static List<DocumentChangeInfo> DetectChanges(WorkspaceInfo workspaceInfo, SnapshotManifest previousManifest)
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

    private HashSet<string> IdentifyAffectedProjects(Solution solution, HashSet<string> changedPaths)
    {
        var affected = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath == null)
                    continue;

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

    private static string GetRelativePath(string fullPath, string gitRoot)
    {
        var normalizedRoot = Path.GetFullPath(gitRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var root = normalizedRoot + Path.DirectorySeparatorChar;

        return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
    }
}
