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

        Console.Write("Loading solution... ");

        using var workspace = MSBuildWorkspace.Create();

        var solution = await workspace.OpenSolutionAsync(solutionPath);

        Console.WriteLine($"done ({solution.Projects.Count()} projects).");

        var gitRoot = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;

        Console.Write("Building workspace info... ");

        var workspaceInfo = new WorkspaceInfo(solution, gitRoot);

        Console.WriteLine("done.");

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
            await RunFullIndexAsync(store, solution, workspaceInfo, skipAdapters, jsonExportPath);
        }

        Console.Write("Pruning old snapshots... ");

        store.PruneOldSnapshots(keep: 3);

        Console.WriteLine("done.");

        totalSw.Stop();

        Console.WriteLine($"  Total time (full rebuild): {totalSw.ElapsedMilliseconds} ms");
    }

    private static async Task RunFullIndexAsync(IIndexStore store, Solution solution, WorkspaceInfo workspaceInfo, HashSet<string> skipAdapters, string? jsonExportPath)
    {
        var snapshotId = SnapshotId.New();
        var manifest = SnapshotManifest.FromWorkspace(workspaceInfo, snapshotId);
        var snapshotIdStr = snapshotId.ToString();

        Console.Write("Saving snapshot to database... ");

        manifest.Save(store, store, workspaceInfo.DocumentContents, jsonExportPath);

        Console.WriteLine("done.");

        store.MarkSnapshotInProgress(snapshotIdStr);

        try
        {
            int totalDeclarations = 0;
            int totalEdges = 0;
            int totalDiagnostics = 0;

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

            Console.WriteLine();
            Console.WriteLine($"Index complete for snapshot {snapshotIdStr}");
            Console.WriteLine($"  Declarations: {totalDeclarations}");
            Console.WriteLine($"  Edges:        {totalEdges}");
            Console.WriteLine($"  Diagnostics:  {totalDiagnostics}");
            Console.WriteLine($"  Schema v{VersionConstants.DatabaseSchemaVersion}");

            var previousManifest = store.LoadLatestSnapshot(manifest.WorkspaceId.Value);

            if (previousManifest != null && previousManifest.SnapshotId != snapshotIdStr)
            {
                Console.WriteLine();
                Console.Write("Computing semantic diff from previous snapshot... ");

                var differ = new SemanticDiffer(store, store, store);
                var diffChanges = differ.ComputeDiff(previousManifest.SnapshotId, snapshotIdStr);

                store.SaveSemanticChanges(previousManifest.SnapshotId, snapshotIdStr, diffChanges);

                Console.WriteLine($"done ({diffChanges.Count} changes).");
            }

            store.MarkSnapshotComplete(snapshotIdStr);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Full index failed, snapshot {snapshotIdStr} left in 'in_progress' state: {ex.Message}");
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
