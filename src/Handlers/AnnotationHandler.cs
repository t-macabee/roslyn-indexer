using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Handlers;

internal static class AnnotationHandler
{
    public static void RunAnnotate(string[] args)
    {
        var symbolArg = RequireArg(args, "--symbol=", "ERROR: --symbol=<symbolId> is required for --mode=annotate.");
        var kindArg = RequireArg(args, "--kind=", "ERROR: --kind=<kind> is required for --mode=annotate.");
        var valueArg = RequireArg(args, "--value=", "ERROR: --value=<text> is required for --mode=annotate.");

        var outputDirArg = GetArgValue(args, "--output-dir=") ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
        if (string.IsNullOrEmpty(outputDirArg))
        {
            Console.Error.WriteLine("ERROR: --output-dir=path or INDEXER_OUTPUT_DIR is required.");
            Environment.Exit(1);
        }

        var snapshotArg = GetArgValue(args, "--snapshot=");

        var dbPath = Path.Combine(Path.GetFullPath(outputDirArg!), "index.db");
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine("ERROR: Index database not found at " + dbPath);
            Environment.Exit(1);
        }

        var store = new SqliteIndexStore(dbPath);
        store.Open(dbPath);

        try
        {
            var snapshotId = ResolveSnapshotId(store, snapshotArg);
            var annotation = new AnnotationRecord(symbolArg!, kindArg!, valueArg!);
            store.SaveAnnotations(snapshotId, new[] { annotation });

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "ok",
                snapshot_id = snapshotId,
                symbol_id = symbolArg,
                kind = kindArg,
                value = valueArg
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            store.Close();
        }
    }

    public static void RunGetAnnotations(string[] args)
    {
        var symbolArg = GetArgValue(args, "--symbol=");
        // --symbol is optional for get-annotations; when absent, list all annotations

        var outputDirArg = GetArgValue(args, "--output-dir=") ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
        if (string.IsNullOrEmpty(outputDirArg))
        {
            Console.Error.WriteLine("ERROR: --output-dir=path or INDEXER_OUTPUT_DIR is required.");
            Environment.Exit(1);
        }

        var snapshotArg = GetArgValue(args, "--snapshot=");

        var dbPath = Path.Combine(Path.GetFullPath(outputDirArg!), "index.db");
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine("ERROR: Index database not found at " + dbPath);
            Environment.Exit(1);
        }

        var store = new SqliteIndexStore(dbPath);
        store.Open(dbPath);

        try
        {
            var snapshotId = ResolveSnapshotId(store, snapshotArg);
            var annotations = store.GetAnnotations(snapshotId, string.IsNullOrEmpty(symbolArg) ? null : symbolArg);

            var result = new
            {
                snapshot_id = snapshotId,
                symbol_id = symbolArg,
                annotations = annotations.Select(a => new
                {
                    symbol_id = a.SymbolId,
                    kind = a.Kind,
                    value = a.Value
                }).ToList()
            };

            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            store.Close();
        }
    }

    private static string ResolveSnapshotId(SqliteIndexStore store, string? snapshotArg)
    {
        if (!string.IsNullOrEmpty(snapshotArg))
            return snapshotArg;

        var snapshotId = store.GetLatestSnapshotId();
        if (snapshotId == null)
        {
            Console.Error.WriteLine("ERROR: No snapshots found in the database.");
            Environment.Exit(1);
        }

        return snapshotId!;
    }

    private static string? RequireArg(string[] args, string prefix, params string[] errorLines)
    {
        var value = GetArgValue(args, prefix);
        if (string.IsNullOrEmpty(value))
        {
            foreach (var line in errorLines)
                Console.Error.WriteLine(line);
            Environment.Exit(1);
        }

        return value;
    }

    private static string? GetArgValue(string[] args, string prefix)
    {
        return args.FirstOrDefault(a => a.StartsWith(prefix))?.Split('=', 2)[1];
    }
}
