using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class DiffHandler
{
    public static void Run(string[] args)
    {
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

        var fromSnapshot = GetArgValue(args, "--from-snapshot=");
        var toSnapshot = GetArgValue(args, "--to-snapshot=");
        if (string.IsNullOrEmpty(fromSnapshot) || string.IsNullOrEmpty(toSnapshot))
        {
            Console.Error.WriteLine("ERROR: --from-snapshot=<id> and --to-snapshot=<id> are required for --mode=diff.");
            Environment.Exit(1);
        }

        var store = new SqliteIndexStore(dbPath);
        store.Open(dbPath);

        try
        {
            var differ = new SemanticDiffer(store, store, store);
            var changes = differ.ComputeDiff(fromSnapshot, toSnapshot);

            var json = JsonSerializer.Serialize(new
            {
                from_snapshot = fromSnapshot,
                to_snapshot = toSnapshot,
                change_count = changes.Count,
                changes = changes.Select(c => new
                {
                    change_id = c.ChangeId,
                    change_type = c.ChangeType,
                    symbol_id = c.SymbolId,
                    detail = c.DetailJson != null ? JsonSerializer.Deserialize<object>(c.DetailJson) : null,
                    created_at_utc = c.CreatedAtUtc
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
