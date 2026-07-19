using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class IndexHandler
{
    public static async Task Run(string[] args)
    {
        var solutionPathArg = GetArgValue(args, "--solution=") ?? Environment.GetEnvironmentVariable("INDEXER_SOLUTION_PATH");
        if (string.IsNullOrEmpty(solutionPathArg) || !File.Exists(solutionPathArg))
        {
            Console.Error.WriteLine("ERROR: --solution=path or INDEXER_SOLUTION_PATH is required and must point to an existing .sln file.");
            Environment.Exit(1);
        }

        var outputDirArg = GetArgValue(args, "--output-dir=") ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
        if (string.IsNullOrEmpty(outputDirArg))
        {
            Console.Error.WriteLine("ERROR: --output-dir=path or INDEXER_OUTPUT_DIR is required.");
            Environment.Exit(1);
        }

        var outputDir = Path.GetFullPath(outputDirArg);
        var dbPath = Path.Combine(outputDir, "index.db");
        var jsonExportPath = GetArgValue(args, "--output-json=");

        var skipAdapters = args.Where(a => a.StartsWith("--skip-adapter="))
                               .Select(a => a.Split('=', 2)[1])
                               .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (skipAdapters.Count > 0)
        {
            var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ASP.NET Core", "Dependency Injection", "MediatR", "EF Core", "Serialization", "Test"
            };
            foreach (var name in skipAdapters)
            {
                if (!knownNames.Contains(name))
                    Console.WriteLine($"WARNING: Unknown adapter name '{name}'. Valid names: {string.Join(", ", knownNames)}");
            }
            Console.WriteLine($"Skipping adapters: {string.Join(", ", skipAdapters)}");
        }

        var strategyArg = GetArgValue(args, "--strategy=");

        Console.WriteLine($"Solution: {solutionPathArg}");
        Console.WriteLine($"Output DB: {dbPath}");
        if (jsonExportPath != null)
            Console.WriteLine($"JSON export: {jsonExportPath}");
        Console.WriteLine();

        var store = new SqliteIndexStore(dbPath);
        store.Open(dbPath);
        store.RunMigrations();
        store.ValidateSchema(expectedVersion: VersionConstants.DatabaseSchemaVersion);

        try
        {
            await IndexRunner.RunAsync(store, solutionPathArg, outputDir, skipAdapters, jsonExportPath, strategyArg);
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
