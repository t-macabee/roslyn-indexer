using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Handlers;

internal static class TimingsHandler
{
    public static void Run(string[] args)
    {
        var outputDirArg = GetArgValue(args, "--output-dir=") ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
        if (string.IsNullOrEmpty(outputDirArg))
        {
            Console.Error.WriteLine("ERROR: --output-dir=path or INDEXER_OUTPUT_DIR is required.");
            Environment.Exit(1);
        }

        var dbPath = Path.Combine(Path.GetFullPath(outputDirArg!), "index.db");
        var asJson = args.Contains("--json");
        var snapshotId = GetArgValue(args, "--snapshot=");

        if (!File.Exists(dbPath))
        {
            if (asJson)
                Console.WriteLine(JsonSerializer.Serialize(new { error = "Database not found", database_path = dbPath }, new JsonSerializerOptions { WriteIndented = true }));
            else
                Console.WriteLine("Database not found. Run --mode=index first.");
            return;
        }

        var store = new SqliteIndexStore(dbPath);
        store.Open(dbPath);

        try
        {
            if (snapshotId != null)
            {
                ShowTimingsForSnapshot(store, snapshotId, asJson);
            }
            else
            {
                ShowLatestTimings(store, asJson);
            }
        }
        finally
        {
            store.Close();
        }
    }

    private static void ShowTimingsForSnapshot(IIndexStore store, string snapshotId, bool asJson)
    {
        var timings = store.GetTimings(snapshotId);

        if (timings.Count == 0)
        {
            if (asJson)
                Console.WriteLine(JsonSerializer.Serialize(new { snapshot_id = snapshotId, timings = Array.Empty<object>(), note = "No timing data for this snapshot." }, new JsonSerializerOptions { WriteIndented = true }));
            else
                Console.WriteLine($"No timing data for snapshot {snapshotId}.");
            return;
        }

        if (asJson)
        {
            var output = new
            {
                snapshot_id = snapshotId,
                total_ms = timings.Sum(t => t.ElapsedMs),
                steps = timings.Select(t => new { step = t.StepName, elapsed_ms = t.ElapsedMs, percent = timings.Sum(x => x.ElapsedMs) > 0 ? Math.Round((double)t.ElapsedMs / timings.Sum(x => x.ElapsedMs) * 100, 1) : 0 })
            };
            Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            var totalMs = timings.Sum(t => t.ElapsedMs);
            Console.WriteLine($"Timings for snapshot {snapshotId}");
            Console.WriteLine(new string('-', 65));
            Console.WriteLine($"{"Step",-40} {"Elapsed (ms)",-12} {"%",-6}");
            Console.WriteLine(new string('-', 65));

            foreach (var t in timings)
            {
                var pct = totalMs > 0 ? (double)t.ElapsedMs / totalMs * 100 : 0;
                Console.WriteLine($"{t.StepName,-40} {t.ElapsedMs,12} {pct,5:F1}%");
            }
            Console.WriteLine(new string('-', 65));
            Console.WriteLine($"{"Total",-40} {totalMs,12}");
        }
    }

    private static void ShowLatestTimings(IIndexStore store, bool asJson)
    {
        var latestSnapshotId = store.GetLatestSnapshotId();
        if (latestSnapshotId == null)
        {
            if (asJson)
                Console.WriteLine(JsonSerializer.Serialize(new { error = "No snapshots found" }, new JsonSerializerOptions { WriteIndented = true }));
            else
                Console.WriteLine("No snapshots found. Run --mode=index first.");
            return;
        }

        ShowTimingsForSnapshot(store, latestSnapshotId, asJson);
    }

    private static string? GetArgValue(string[] args, string prefix)
    {
        return args.FirstOrDefault(a => a.StartsWith(prefix))?.Split('=', 2)[1];
    }
}
