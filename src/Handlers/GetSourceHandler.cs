using Lurp.Storage;

namespace Lurp.Handlers;

public static class GetSourceHandler
{
    public static void Run(string[] args)
    {
        var documentArg = GetArgValue(args, "--document=");
        if (string.IsNullOrEmpty(documentArg))
        {
            Console.Error.WriteLine("ERROR: --document=<relative-path> is required for --mode=get-source.");
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
            string? source;
            if (!string.IsNullOrEmpty(snapshotArg))
            {
                source = store.GetSource(documentArg, snapshotArg);
            }
            else
            {
                var latestSnapshot = store.GetLatestSnapshotId();
                if (latestSnapshot == null)
                {
                    Console.Error.WriteLine("ERROR: No snapshots found in the database.");
                    Environment.Exit(1);
                }
                source = store.GetSource(documentArg, latestSnapshot);
            }

            if (source == null)
            {
                Console.Error.WriteLine($"ERROR: Document '{documentArg}' not found in snapshot.");
                Environment.Exit(1);
            }

            Console.Write(source);
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
