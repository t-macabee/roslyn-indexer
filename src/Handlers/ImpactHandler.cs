using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Handlers;

public static class ImpactHandler
{
    public static void Run(string[] args)
    {
        var symbolArg = GetArgValue(args, "--symbol=");
        if (string.IsNullOrEmpty(symbolArg))
        {
            Console.Error.WriteLine("ERROR: --symbol=<symbol-id> is required for --mode=impact.");
            Environment.Exit(1);
        }

        var directionArg = GetArgValue(args, "--direction=") ?? "downstream";
        ImpactDirection direction = directionArg.ToLowerInvariant() switch
        {
            "downstream" => ImpactDirection.Downstream,
            "upstream" => ImpactDirection.Upstream,
            _ => throw new ArgumentException($"Invalid direction '{directionArg}'. Use 'upstream' or 'downstream'.")
        };

        var snapshotArg = GetArgValue(args, "--snapshot=");
        var maxDepthArg = GetArgValue(args, "--max-depth=");
        int maxDepth = 10;
        if (!string.IsNullOrEmpty(maxDepthArg) && (!int.TryParse(maxDepthArg, out maxDepth) || maxDepth < 1))
        {
            Console.Error.WriteLine("ERROR: --max-depth must be a positive integer.");
            Environment.Exit(1);
        }

        var kindsArg = GetArgValue(args, "--kinds=");
        HashSet<string>? allowedKinds = !string.IsNullOrEmpty(kindsArg)
            ? [.. kindsArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : null;

        var outputDirArg = GetArgValue(args, "--output-dir=") ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
        if (string.IsNullOrEmpty(outputDirArg))
        {
            Console.Error.WriteLine("ERROR: --output-dir=path or INDEXER_OUTPUT_DIR is required.");
            Environment.Exit(1);
        }

        var dbPath = Path.Combine(Path.GetFullPath(outputDirArg), "index.db");
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine("ERROR: Index database not found at " + dbPath);
            Environment.Exit(1);
        }

        var store = new SqliteIndexStore(dbPath);
        store.Open(dbPath);

        try
        {
            var snapshotId = snapshotArg;
            if (string.IsNullOrEmpty(snapshotId))
            {
                snapshotId = store.GetLatestSnapshotId();
                if (snapshotId == null)
                {
                    Console.Error.WriteLine("ERROR: No snapshots found in the database.");
                    Environment.Exit(1);
                }
            }

            var traverser = new ImpactTraverser(store, snapshotId);
            var paths = traverser.TraceImpact(symbolId: symbolArg, direction: direction, allowedEdgeKinds: allowedKinds, maxDepth: maxDepth, includeSource: true);

            var json = JsonSerializer.Serialize(new
            {
                snapshot_id = snapshotId,
                symbol_id = symbolArg,
                direction = direction == ImpactDirection.Downstream ? "downstream" : "upstream",
                max_depth = maxDepth,
                paths = paths.Select(p => new
                {
                    truncated = p.Truncated,
                    truncation_reason = p.TruncationReason,
                    total_steps = p.TotalSteps,
                    hops = p.Hops.Select(h => new { source_symbol_id = h.SourceSymbolId, target_symbol_id = h.TargetSymbolId, edge_kind = h.EdgeKind, provenance = h.Provenance, source_document = h.SourceDocument, source_line = h.SourceLine })
                })
            }, new JsonSerializerOptions { WriteIndented = true });

            Console.WriteLine(json);
        }
        finally
        {
            store.Close();
        }
    }

    private static string? GetArgValue(string[] args, string prefix)
    {
        return args.FirstOrDefault(a => a.StartsWith(prefix))?.Split('=', 2)[1];
    }
}
