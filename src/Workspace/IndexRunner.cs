using Lurp.Storage;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using System.Diagnostics;

namespace Lurp.Workspace;

public static class IndexRunner
{
    public static async Task RunAsync(IIndexStore store, string solutionPath, string outputDir, HashSet<string> skipAdapters, string? jsonExportPath, string? strategyArg)
    {
        if (!MSBuildLocator.IsRegistered)
        {
            var instances = MSBuildLocator.RegisterDefaults();

            Console.WriteLine($"MSBuild: {instances?.MSBuildPath ?? "default"}");
        }

        string strategy = ResolveStrategy(store, strategyArg);

        Console.WriteLine($"Strategy: {strategy}");

        if (strategy == "full")
        {
            Console.WriteLine("  (Use --strategy=full to force a full rebuild when something looks wrong.)");
        }

        var totalSw = Stopwatch.StartNew();

        var swSolutionLoad = Stopwatch.StartNew();
        Console.Write("Loading solution... ");

        using var workspace = MSBuildWorkspace.Create();

        var solution = await workspace.OpenSolutionAsync(solutionPath);

        Console.WriteLine($"done ({solution.Projects.Count()} projects).");
        swSolutionLoad.Stop();

        var gitRoot = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;

        var swWorkspaceInfo = Stopwatch.StartNew();
        Console.Write("Building workspace info... ");

        var workspaceInfo = new WorkspaceInfo(solution, gitRoot);

        Console.WriteLine("done.");
        swWorkspaceInfo.Stop();

        if (strategy == "incremental")
        {
            var previousStorageManifest = store.LoadLatestSnapshot(workspaceInfo.Id.Value);

            if (previousStorageManifest == null)
            {
                Console.WriteLine("No previous snapshot found. Falling back to full index.");
                strategy = "full";
            }
            else
            {
                var incrementalIndexer = new IncrementalIndexer(store, gitRoot, solutionPath, outputDir, skipAdapters, jsonExportPath);
                var result = await incrementalIndexer.RunIncrementalAsync(solution, workspaceInfo, previousStorageManifest);

                Console.WriteLine();
                Console.WriteLine($"Incremental index complete. Snapshot: {result.NewSnapshotId}");
                Console.WriteLine($"  Previous snapshot: {result.PreviousSnapshotId}");
                Console.WriteLine($"  Changed documents: {result.ChangedDocumentCount}");
                Console.WriteLine($"  Declarations:      {result.DeclarationsExtracted}");
                Console.WriteLine($"  Edges:             {result.EdgesExtracted}");
                Console.WriteLine($"  Diagnostics:       {result.DiagnosticsExtracted}");
                Console.WriteLine($"  Schema v{VersionConstants.DatabaseSchemaVersion}");
                Console.Write("Pruning old snapshots... ");

                store.PruneOldSnapshots(keep: 3);

                Console.WriteLine("done.");

                totalSw.Stop();

                Console.WriteLine($"  Total time (incremental): {totalSw.ElapsedMilliseconds} ms");
                return;
            }
        }

        if (strategy == "full")
        {
            var setupTimings = new List<SnapshotTimingRow>
            {
                new SnapshotTimingRow("solution_load", swSolutionLoad.ElapsedMilliseconds, DateTime.UtcNow),
                new SnapshotTimingRow("workspace_info", swWorkspaceInfo.ElapsedMilliseconds, DateTime.UtcNow),
            };
            await RunFullIndexAsync(store, solution, workspaceInfo, skipAdapters, jsonExportPath, setupTimings);
        }

        Console.Write("Pruning old snapshots... ");

        store.PruneOldSnapshots(keep: 3);

        Console.WriteLine("done.");

        totalSw.Stop();

        Console.WriteLine($"  Total time (full rebuild): {totalSw.ElapsedMilliseconds} ms");
    }

    private static async Task RunFullIndexAsync(IIndexStore store, Solution solution, WorkspaceInfo workspaceInfo, HashSet<string> skipAdapters, string? jsonExportPath, List<SnapshotTimingRow>? setupTimings = null)
    {
        var snapshotId = SnapshotId.New();
        var manifest = SnapshotManifest.FromWorkspace(workspaceInfo, snapshotId);
        var snapshotIdStr = snapshotId.ToString();
        var timings = setupTimings != null ? new List<SnapshotTimingRow>(setupTimings) : new List<SnapshotTimingRow>();

        // Step: Manifest Save (includes initial FTS build)
        var swManifest = Stopwatch.StartNew();
        Console.Write("Saving snapshot to database... ");

        manifest.Save(store, store, workspaceInfo.DocumentContents, jsonExportPath);

        Console.WriteLine("done.");
        swManifest.Stop();
        timings.Add(new SnapshotTimingRow("manifest_save", swManifest.ElapsedMilliseconds, DateTime.UtcNow));

        store.MarkSnapshotInProgress(snapshotIdStr);

        try
        {
            int totalDeclarations = 0;
            int totalEdges = 0;
            int totalDiagnostics = 0;

            // Step: Full Extraction Loop (compilation load + fact extraction + db writes)
            var swExtract = Stopwatch.StartNew();
            await foreach (var (project, compilation) in CompilationHelper.GetAllAsync(solution))
            {
                var projectName = project.Name;

                Console.Write($"  [{projectName}] ");

                var result = CompilationFactExtractor.ExtractAll(compilation, workspaceInfo, snapshotIdStr, projectName, skipAdapters, logWarning: msg => Console.Error.WriteLine($"WARNING: {msg}"), logError: msg => Console.Error.WriteLine($"ERROR: {msg}"));

                store.SaveDeclarations(snapshotIdStr, result.Declarations);
                totalDeclarations += result.Declarations.Count;

                store.SaveEdges(snapshotIdStr, result.Edges);
                totalEdges += result.Edges.Count;

                store.SaveDiagnostics(snapshotIdStr, result.Diagnostics);
                totalDiagnostics += result.Diagnostics.Count;

                Console.WriteLine($"{result.Declarations.Count} symbols, {result.Edges.Count} edges, {result.Diagnostics.Count} diagnostics.");
            }
            swExtract.Stop();
            timings.Add(new SnapshotTimingRow("extraction_loop", swExtract.ElapsedMilliseconds, DateTime.UtcNow));

            Console.WriteLine();
            Console.WriteLine($"Index complete for snapshot {snapshotIdStr}");
            Console.WriteLine($"  Declarations: {totalDeclarations}");
            Console.WriteLine($"  Edges:        {totalEdges}");
            Console.WriteLine($"  Diagnostics:  {totalDiagnostics}");
            Console.WriteLine($"  Schema v{VersionConstants.DatabaseSchemaVersion}");

            var previousManifest = store.LoadLatestSnapshot(manifest.WorkspaceId.Value);

            if (previousManifest != null && previousManifest.SnapshotId != snapshotIdStr)
            {
                // Step: Semantic Diff
                var swDiff = Stopwatch.StartNew();
                Console.WriteLine();
                Console.Write("Computing semantic diff from previous snapshot... ");

                var differ = new SemanticDiffer(store, store, store);
                var diffChanges = differ.ComputeDiff(previousManifest.SnapshotId, snapshotIdStr);

                store.SaveSemanticChanges(previousManifest.SnapshotId, snapshotIdStr, diffChanges);

                Console.WriteLine($"done ({diffChanges.Count} changes).");
                swDiff.Stop();
                timings.Add(new SnapshotTimingRow("semantic_diff", swDiff.ElapsedMilliseconds, DateTime.UtcNow));
            }

            store.MarkSnapshotComplete(snapshotIdStr);

            // Persist all timings
            try { store.SaveTimings(snapshotIdStr, timings); }
            catch (Exception ex) { Console.Error.WriteLine($"WARNING: Failed to save timings: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Full index failed, snapshot {snapshotIdStr} left in 'in_progress' state: {ex.Message}");

            // Try to save whatever timings we have
            try { store.SaveTimings(snapshotIdStr, timings); }
            catch { }

            throw;
        }
    }

    private static string ResolveStrategy(IIndexStore store, string? strategyArg)
    {
        if (strategyArg != null)
        {
            var strategy = strategyArg.ToLowerInvariant();

            if (strategy != "incremental" && strategy != "full")
            {
                Console.Error.WriteLine("ERROR: --strategy must be 'incremental' or 'full'.");
                Environment.Exit(1);
            }
            return strategy;
        }

        var latestSnapshotId = store.GetLatestSnapshotId();

        if (latestSnapshotId == null)
        {
            Console.WriteLine("No existing snapshot found. Defaulting to --strategy=full for initial index.");
            return "full";
        }

        return "incremental";
    }
}
