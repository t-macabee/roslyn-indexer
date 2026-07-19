using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class ContextHandler
{
    public static void Run(string[] args)
    {
        var symbolArg = GetArgValue(args, "--symbol=");
        var fileArg = GetArgValue(args, "--file=");
        var lineArg = GetArgValue(args, "--line=");
        var intentArg = GetArgValue(args, "--intent=") ?? "inspect";
        var budgetArg = GetArgValue(args, "--budget=");
        var snapshotArg = GetArgValue(args, "--snapshot=");
        var maxHopsArg = GetArgValue(args, "--max-hops=");
        var includeGenerated = args.Contains("--include-generated");
        var outputDirArg = GetArgValue(args, "--output-dir=") ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");

        if (string.IsNullOrEmpty(outputDirArg))
        {
            Console.Error.WriteLine("ERROR: --output-dir=path or INDEXER_OUTPUT_DIR is required.");
            Environment.Exit(1);
        }

        bool hasSymbol = !string.IsNullOrEmpty(symbolArg);
        bool hasFile = !string.IsNullOrEmpty(fileArg) && !string.IsNullOrEmpty(lineArg);
        if (!hasSymbol && !hasFile)
        {
            Console.Error.WriteLine("ERROR: Either --symbol=<symbolId> or --file=<path> --line=<line> is required for --mode=context.");
            Environment.Exit(1);
        }

        ContextIntent intent = intentArg.ToLowerInvariant() switch
        {
            "inspect" => ContextIntent.Inspect,
            "modify" => ContextIntent.Modify,
            "diagnose" => ContextIntent.Diagnose,
            _ => throw new ArgumentException("--intent must be one of: inspect, modify, diagnose.")
        };

        int budget = 8000;
        if (!string.IsNullOrEmpty(budgetArg) && (!int.TryParse(budgetArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out budget) || budget < 1))
        {
            Console.Error.WriteLine("ERROR: --budget must be a positive integer.");
            Environment.Exit(1);
        }

        int maxHops = 3;
        if (!string.IsNullOrEmpty(maxHopsArg) && (!int.TryParse(maxHopsArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxHops) || maxHops < 1))
        {
            Console.Error.WriteLine("ERROR: --max-hops must be a positive integer.");
            Environment.Exit(1);
        }

        int? lineNumber = null;
        if (hasFile)
        {
            if (!int.TryParse(lineArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ln) || ln < 1)
            {
                Console.Error.WriteLine("ERROR: --line must be a positive integer.");
                Environment.Exit(1);
            }
            lineNumber = ln;
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

            var lookup = new ContextLookup(snapshotId, symbolArg, fileArg, lineNumber);
            var assemblyOptions = new ContextAssemblyOptions(intent, budget, maxHops, includeGenerated);
            var capsule = ContextAssembler.ResolveAndAssemble(store, lookup, assemblyOptions);
            WriteCapsuleOutput(capsule, outputDirArg);
        }
        finally
        {
            store.Close();
        }
    }

    private static void WriteCapsuleOutput(ContextCapsule capsule, string outputDirArg)
    {
        var json = JsonSerializer.Serialize(capsule, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        var safeName = capsule.Anchor.SymbolId
            .Replace('|', '_')
            .Replace(':', '_')
            .Replace('/', '_')
            .Replace('\\', '_');
        var outputFileName = $"capsule-{safeName}.json";
        var outputPath = Path.Combine(Path.GetFullPath(outputDirArg), outputFileName);
        File.WriteAllText(outputPath, json);
        Console.WriteLine(json);
    }

    private static string? GetArgValue(string[] args, string prefix)
    {
        return args.FirstOrDefault(a => a.StartsWith(prefix))?.Split('=', 2)[1];
    }
}
