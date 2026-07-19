using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Handlers;

public static class SimulateRemoveHandler
{
    public static void Run(string[] args)
    {
        var symbolArg = GetArgValue(args, "--symbol=");
        if (string.IsNullOrEmpty(symbolArg))
        {
            Console.Error.WriteLine("ERROR: --symbol=<symbol-id> is required for --mode=simulate-remove.");
            Environment.Exit(1);
        }

        var snapshotArg = GetArgValue(args, "--snapshot=");
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

            var engine = new SimulationEngine(store, store, snapshotId);
            var report = engine.SimulateRemove(symbolArg);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
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
