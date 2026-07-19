using System.Globalization;
using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Handlers;

public static class GetSymbolHandler
{
    public static void Run(string[] args)
    {
        var symbolArg = GetArgValue(args, "--symbol=");
        if (string.IsNullOrEmpty(symbolArg))
        {
            Console.Error.WriteLine("ERROR: --symbol=<symbolId> is required for --mode=get-symbol.");
            Environment.Exit(1);
        }

        var viewArg = GetArgValue(args, "--view=");
        if (string.IsNullOrEmpty(viewArg))
        {
            Console.Error.WriteLine("ERROR: --view=<view-kind> is required for --mode=get-symbol.");
            Console.Error.WriteLine("  Valid values: metadata, signature, body, declaration, containing-type, surrounding");
            Environment.Exit(1);
        }

        var outputDirArg = GetArgValue(args, "--output-dir=") ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
        if (string.IsNullOrEmpty(outputDirArg))
        {
            Console.Error.WriteLine("ERROR: --output-dir=path or INDEXER_OUTPUT_DIR is required.");
            Environment.Exit(1);
        }

        var snapshotArg = GetArgValue(args, "--snapshot=");
        var contextLinesArg = GetArgValue(args, "--context-lines=");
        var includeGenerated = args.Contains("--include-generated");

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

            ViewKind viewKind = ViewKind.Declaration;
            bool isMetadataView = false;
            bool isContainingType = false;
            bool isSurrounding = false;
            int contextLines = 3;

            switch (viewArg.ToLowerInvariant())
            {
                case "metadata":
                    isMetadataView = true;
                    break;
                case "signature":
                    viewKind = ViewKind.Signature;
                    break;
                case "body":
                    viewKind = ViewKind.Body;
                    break;
                case "declaration":
                    viewKind = ViewKind.Declaration;
                    break;
                case "containing-type":
                    isContainingType = true;
                    break;
                case "surrounding":
                    isSurrounding = true;
                    if (!string.IsNullOrEmpty(contextLinesArg) && int.TryParse(contextLinesArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        contextLines = parsed;
                    break;
                default:
                    Console.Error.WriteLine($"ERROR: Unknown view kind '{viewArg}'.");
                    Console.Error.WriteLine("  Valid values: metadata, signature, body, declaration, containing-type, surrounding");
                    Environment.Exit(1);
                    return;
            }

            if (isMetadataView)
            {
                var info = store.GetSymbolInfo(symbolArg, snapshotId);
                if (info == null)
                {
                    Console.Error.WriteLine($"ERROR: Symbol '{symbolArg}' not found in snapshot '{snapshotId}'.");
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
            else if (isContainingType)
            {
                var source = store.GetContainingTypeSource(symbolArg, snapshotId);
                if (source == null)
                {
                    Console.Error.WriteLine($"ERROR: Containing type source not found for symbol '{symbolArg}'.");
                    Environment.Exit(1);
                }
                Console.Write(source);
            }
            else if (isSurrounding)
            {
                var source = store.GetSurroundingLines(symbolArg, snapshotId, contextLines);
                if (source == null)
                {
                    Console.Error.WriteLine($"ERROR: Surrounding lines not found for symbol '{symbolArg}'.");
                    Environment.Exit(1);
                }
                Console.Write(source);
            }
            else
            {
                var source = store.GetSymbolSource(symbolArg, snapshotId, viewKind, includeGenerated);
                if (source == null)
                {
                    Console.Error.WriteLine($"ERROR: Source not found for symbol '{symbolArg}' with view '{viewArg}'.");
                    Environment.Exit(1);
                }
                Console.Write(source);
            }
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
