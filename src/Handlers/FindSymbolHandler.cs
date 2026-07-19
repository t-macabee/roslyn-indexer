using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Handlers;

public static class FindSymbolHandler
{
    public static void Run(string[] args)
    {
        var fqnArg = GetArgValue(args, "--fqn=");
        if (string.IsNullOrEmpty(fqnArg))
        {
            Console.Error.WriteLine("ERROR: --fqn=<name> is required for --mode=find-symbol.");
            Environment.Exit(1);
        }

        var snapshotArg = GetArgValue(args, "--snapshot=");
        var includeGenerated = args.Contains("--include-generated");
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

            var info = store.ResolveSymbolByFqn(fqnArg, snapshotId, includeGenerated);
            if (info == null)
            {
                Console.Error.WriteLine($"ERROR: Symbol with FQN '{fqnArg}' not found in snapshot '{snapshotId}'.");
                Environment.Exit(1);
            }

            var json = JsonSerializer.Serialize(new
            {
                symbolId = info.SymbolId.Value,
                docCommentId = info.SymbolId.DocCommentId,
                assemblyIdentity = info.SymbolId.AssemblyIdentity,
                kind = info.Kind.ToString(),
                fullyQualifiedName = info.FullyQualifiedName,
                metadataJson = info.MetadataJson,
                declarationCount = info.DeclarationCount,
                isPartial = info.IsPartial,
                snapshotId
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
