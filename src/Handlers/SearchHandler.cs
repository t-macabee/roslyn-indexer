using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Handlers;

public static class SearchHandler
{
    public static void Run(string[] args)
    {
        var queryArg = GetArgValue(args, "--query=");
        if (string.IsNullOrEmpty(queryArg))
        {
            Console.Error.WriteLine("ERROR: --query=<term> is required for --mode=search.");
            Environment.Exit(1);
        }

        var typeArg = GetArgValue(args, "--type=") ?? "all";
        var snapshotArg = GetArgValue(args, "--snapshot=");
        var limitArg = GetArgValue(args, "--limit=");
        var kindArg = GetArgValue(args, "--kind=");
        var includeGenerated = args.Contains("--include-generated");

        int limit = 20;
        if (!string.IsNullOrEmpty(limitArg) && !int.TryParse(limitArg, out limit))
        {
            Console.Error.WriteLine("ERROR: --limit must be an integer.");
            Environment.Exit(1);
        }

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

            var results = new List<object>();
            if (typeArg == "source" || typeArg == "all")
            {
                var sourceResults = store.SearchSource(queryArg, snapshotId, limit, includeGenerated);
                foreach (var r in sourceResults)
                    results.Add(new { type = "source", documentPath = r.DocumentPath, snippet = r.Snippet });
            }

            if (typeArg == "symbol" || typeArg == "all")
            {
                var symbolResults = store.SearchSymbols(queryArg, snapshotId, limit, includeGenerated, kindArg);
                foreach (var r in symbolResults)
                    results.Add(new { type = "symbol", symbolId = r.SymbolId, fullyQualifiedName = r.FullyQualifiedName, kind = r.Kind, docCommentId = r.DocCommentId });
            }

            var json = JsonSerializer.Serialize(new { snapshotId, query = queryArg, type = typeArg, results }, new JsonSerializerOptions { WriteIndented = true });
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
