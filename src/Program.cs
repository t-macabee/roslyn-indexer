using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Lurp.Adapters;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp
{
    public class Program
    {
        private static IIndexStore? _indexStore;

        public static void Main(string[] args)
        {
            if (args.Contains("--mode=get-source"))
            {
                RunGetSource(args);
                return;
            }

            if (args.Contains("--mode=get-symbol"))
            {
                RunGetSymbol(args);
                return;
            }

            if (args.Contains("--mode=search"))
            {
                RunSearch(args);
                return;
            }

            if (args.Contains("--mode=find-symbol"))
            {
                RunFindSymbol(args);
                return;
            }

            if (args.Contains("--mode=index"))
            {
                RunIndex(args).GetAwaiter().GetResult();
                return;
            }

            if (args.Contains("--mode=diff"))
            {
                RunDiff(args);
                return;
            }

            if (args.Contains("--mode=test-migration"))
            {
                TestMigration();
                return;
            }

            if (args.Contains("--mode=status"))
            {
                var solutionPathArg = args.FirstOrDefault(a => a.StartsWith("--solution="))?.Split('=', 2)[1]
                    ?? Environment.GetEnvironmentVariable("INDEXER_SOLUTION_PATH");
                var outputDirArg = args.FirstOrDefault(a => a.StartsWith("--output-dir="))?.Split('=', 2)[1]
                    ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
                if (!string.IsNullOrEmpty(solutionPathArg) && !string.IsNullOrEmpty(outputDirArg))
                {
                    var dbPath = Path.Combine(Path.GetFullPath(outputDirArg), "index.db");
                    _indexStore = new SqliteIndexStore(dbPath);
                    _indexStore.Open(dbPath);
                    _indexStore.RunMigrations();
                    _indexStore.ValidateSchema(expectedVersion: VersionConstants.DatabaseSchemaVersion);
                    try { ShowStatus(); }
                    finally { _indexStore.Close(); }
                }
                else
                {
                    Console.WriteLine("INDEXER_SOLUTION_PATH and INDEXER_OUTPUT_DIR must be set, or provide --solution= and --output-dir=.");
                }
                return;
            }

            Console.Error.WriteLine("ERROR: Unknown mode. Use --mode=index, --mode=get-source, --mode=get-symbol, --mode=search, --mode=find-symbol, --mode=diff, --mode=status, or --mode=test-migration.");
            Environment.Exit(1);
        }

        private static void RunGetSource(string[] args)
        {
            var documentArg = args.FirstOrDefault(a => a.StartsWith("--document="))?.Split('=', 2)[1];
            if (string.IsNullOrEmpty(documentArg))
            {
                Console.Error.WriteLine("ERROR: --document=<relative-path> is required for --mode=get-source.");
                Environment.Exit(1);
            }

            var snapshotArg = args.FirstOrDefault(a => a.StartsWith("--snapshot="))?.Split('=', 2)[1];

            var outputDirArg = args.FirstOrDefault(a => a.StartsWith("--output-dir="))?.Split('=', 2)[1]
                ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
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

            _indexStore = new SqliteIndexStore(dbPath);
            _indexStore.Open(dbPath);

            try
            {
                
                string? source;
                if (!string.IsNullOrEmpty(snapshotArg))
                {
                    source = _indexStore.GetSource(documentArg, snapshotArg);
                }
                else
                {
                    
                    using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                    connection.Open();
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT snapshot_id FROM snapshots ORDER BY built_at_utc DESC LIMIT 1;";
                    var latestSnapshot = cmd.ExecuteScalar() as string;
                    if (latestSnapshot == null)
                    {
                        Console.Error.WriteLine("ERROR: No snapshots found in the database.");
                        Environment.Exit(1);
                    }
                    source = _indexStore.GetSource(documentArg, latestSnapshot);
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
                _indexStore.Close();
            }
        }

        private static void RunGetSymbol(string[] args)
        {
            var symbolArg = args.FirstOrDefault(a => a.StartsWith("--symbol="))?.Split('=', 2)[1];
            if (string.IsNullOrEmpty(symbolArg))
            {
                Console.Error.WriteLine("ERROR: --symbol=<symbolId> is required for --mode=get-symbol.");
                Environment.Exit(1);
            }

            var viewArg = args.FirstOrDefault(a => a.StartsWith("--view="))?.Split('=', 2)[1];
            if (string.IsNullOrEmpty(viewArg))
            {
                Console.Error.WriteLine("ERROR: --view=<view-kind> is required for --mode=get-symbol.");
                Console.Error.WriteLine("  Valid values: metadata, signature, body, declaration, containing-type, surrounding");
                Environment.Exit(1);
            }

            var outputDirArg = args.FirstOrDefault(a => a.StartsWith("--output-dir="))?.Split('=', 2)[1]
                ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
            if (string.IsNullOrEmpty(outputDirArg))
            {
                Console.Error.WriteLine("ERROR: --output-dir=path or INDEXER_OUTPUT_DIR is required.");
                Environment.Exit(1);
            }

            var snapshotArg = args.FirstOrDefault(a => a.StartsWith("--snapshot="))?.Split('=', 2)[1];
            var contextLinesArg = args.FirstOrDefault(a => a.StartsWith("--context-lines="))?.Split('=', 2)[1];
            var includeGenerated = args.Contains("--include-generated");

            var dbPath = Path.Combine(Path.GetFullPath(outputDirArg), "index.db");
            if (!File.Exists(dbPath))
            {
                Console.Error.WriteLine("ERROR: Index database not found at " + dbPath);
                Environment.Exit(1);
            }

            _indexStore = new SqliteIndexStore(dbPath);
            _indexStore.Open(dbPath);

            try
            {
                var snapshotId = snapshotArg;
                if (string.IsNullOrEmpty(snapshotId))
                {
                    using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                    connection.Open();
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT snapshot_id FROM snapshots ORDER BY built_at_utc DESC LIMIT 1;";
                    var latestSnapshot = cmd.ExecuteScalar() as string;
                    if (latestSnapshot == null)
                    {
                        Console.Error.WriteLine("ERROR: No snapshots found in the database.");
                        Environment.Exit(1);
                    }
                    snapshotId = latestSnapshot;
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
                        if (!string.IsNullOrEmpty(contextLinesArg) && int.TryParse(contextLinesArg, out var parsed))
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
                    var info = _indexStore.GetSymbolInfo(symbolArg, snapshotId);
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
                    var source = _indexStore.GetContainingTypeSource(symbolArg, snapshotId);
                    if (source == null)
                    {
                        Console.Error.WriteLine($"ERROR: Containing type source not found for symbol '{symbolArg}'.");
                        Environment.Exit(1);
                    }
                    Console.Write(source);
                }
                else if (isSurrounding)
                {
                    var source = _indexStore.GetSurroundingLines(symbolArg, snapshotId, contextLines);
                    if (source == null)
                    {
                        Console.Error.WriteLine($"ERROR: Surrounding lines not found for symbol '{symbolArg}'.");
                        Environment.Exit(1);
                    }
                    Console.Write(source);
                }
                else
                {
                    var source = _indexStore.GetSymbolSource(symbolArg, snapshotId, viewKind, includeGenerated);
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
                _indexStore.Close();
            }
        }

        private static void RunSearch(string[] args)
        {
            var queryArg = args.FirstOrDefault(a => a.StartsWith("--query="))?.Split('=', 2)[1];
            if (string.IsNullOrEmpty(queryArg))
            {
                Console.Error.WriteLine("ERROR: --query=<term> is required for --mode=search.");
                Environment.Exit(1);
            }

            var typeArg = args.FirstOrDefault(a => a.StartsWith("--type="))?.Split('=', 2)[1] ?? "all";
            var snapshotArg = args.FirstOrDefault(a => a.StartsWith("--snapshot="))?.Split('=', 2)[1];
            var limitArg = args.FirstOrDefault(a => a.StartsWith("--limit="))?.Split('=', 2)[1];
            var includeGenerated = args.Contains("--include-generated");
            int limit = 20;
            if (!string.IsNullOrEmpty(limitArg) && !int.TryParse(limitArg, out limit))
            {
                Console.Error.WriteLine("ERROR: --limit must be an integer.");
                Environment.Exit(1);
            }

            var outputDirArg = args.FirstOrDefault(a => a.StartsWith("--output-dir="))?.Split('=', 2)[1]
                ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
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

            _indexStore = new SqliteIndexStore(dbPath);
            _indexStore.Open(dbPath);

            try
            {
                var snapshotId = snapshotArg;
                if (string.IsNullOrEmpty(snapshotId))
                {
                    using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                    connection.Open();
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT snapshot_id FROM snapshots ORDER BY built_at_utc DESC LIMIT 1;";
                    var latestSnapshot = cmd.ExecuteScalar() as string;
                    if (latestSnapshot == null)
                    {
                        Console.Error.WriteLine("ERROR: No snapshots found in the database.");
                        Environment.Exit(1);
                    }
                    snapshotId = latestSnapshot;
                }

                var results = new List<object>();

                if (typeArg == "source" || typeArg == "all")
                {
                    var sourceResults = _indexStore.SearchSource(queryArg, snapshotId, limit, includeGenerated);
                    foreach (var r in sourceResults)
                    {
                        results.Add(new { type = "source", documentPath = r.DocumentPath, snippet = r.Snippet });
                    }
                }

                if (typeArg == "symbol" || typeArg == "all")
                {
                    var symbolResults = _indexStore.SearchSymbols(queryArg, snapshotId, limit, includeGenerated);
                    foreach (var r in symbolResults)
                    {
                        results.Add(new { type = "symbol", symbolId = r.SymbolId, fullyQualifiedName = r.FullyQualifiedName, kind = r.Kind, docCommentId = r.DocCommentId });
                    }
                }

                var json = JsonSerializer.Serialize(new { snapshotId, query = queryArg, type = typeArg, results },
                    new JsonSerializerOptions { WriteIndented = true });
                Console.WriteLine(json);
            }
            finally
            {
                _indexStore.Close();
            }
        }

        private static void RunFindSymbol(string[] args)
        {
            var fqnArg = args.FirstOrDefault(a => a.StartsWith("--fqn="))?.Split('=', 2)[1];
            if (string.IsNullOrEmpty(fqnArg))
            {
                Console.Error.WriteLine("ERROR: --fqn=<name> is required for --mode=find-symbol.");
                Environment.Exit(1);
            }

            var snapshotArg = args.FirstOrDefault(a => a.StartsWith("--snapshot="))?.Split('=', 2)[1];
            var includeGenerated = args.Contains("--include-generated");

            var outputDirArg = args.FirstOrDefault(a => a.StartsWith("--output-dir="))?.Split('=', 2)[1]
                ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
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

            _indexStore = new SqliteIndexStore(dbPath);
            _indexStore.Open(dbPath);

            try
            {
                var snapshotId = snapshotArg;
                if (string.IsNullOrEmpty(snapshotId))
                {
                    using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                    connection.Open();
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT snapshot_id FROM snapshots ORDER BY built_at_utc DESC LIMIT 1;";
                    var latestSnapshot = cmd.ExecuteScalar() as string;
                    if (latestSnapshot == null)
                    {
                        Console.Error.WriteLine("ERROR: No snapshots found in the database.");
                        Environment.Exit(1);
                    }
                    snapshotId = latestSnapshot;
                }

                var info = _indexStore.ResolveSymbolByFqn(fqnArg, snapshotId, includeGenerated);
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
                _indexStore.Close();
            }
        }

        private static async Task RunIndex(string[] args)
        {
            var solutionPathArg = args.FirstOrDefault(a => a.StartsWith("--solution="))?.Split('=', 2)[1]
                ?? Environment.GetEnvironmentVariable("INDEXER_SOLUTION_PATH");
            if (string.IsNullOrEmpty(solutionPathArg) || !File.Exists(solutionPathArg))
            {
                Console.Error.WriteLine("ERROR: --solution=path or INDEXER_SOLUTION_PATH is required and must point to an existing .sln file.");
                Environment.Exit(1);
            }

            var outputDirArg = args.FirstOrDefault(a => a.StartsWith("--output-dir="))?.Split('=', 2)[1]
                ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
            if (string.IsNullOrEmpty(outputDirArg))
            {
                Console.Error.WriteLine("ERROR: --output-dir=path or INDEXER_OUTPUT_DIR is required.");
                Environment.Exit(1);
            }

            var outputDir = Path.GetFullPath(outputDirArg);
            var dbPath = Path.Combine(outputDir, "index.db");

            // Optional JSON export
            var jsonExportPath = args.FirstOrDefault(a => a.StartsWith("--output-json="))?.Split('=', 2)[1];

            // B5: --skip-adapter flags (multiple allowed)
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

            Console.WriteLine($"Solution: {solutionPathArg}");
            Console.WriteLine($"Output DB: {dbPath}");
            if (jsonExportPath != null)
                Console.WriteLine($"JSON export: {jsonExportPath}");
            Console.WriteLine();

            // Register MSBuild
            if (!MSBuildLocator.IsRegistered)
            {
                var instances = MSBuildLocator.RegisterDefaults();
                Console.WriteLine($"MSBuild: {instances?.MSBuildPath ?? "default"}");
            }

            // Open store and run migrations
            var store = new SqliteIndexStore(dbPath);
            store.Open(dbPath);
            store.RunMigrations();
            store.ValidateSchema(expectedVersion: VersionConstants.DatabaseSchemaVersion);

            try
            {
                // Open solution
                Console.Write("Loading solution... ");
                using var workspace = MSBuildWorkspace.Create();
                var solution = await workspace.OpenSolutionAsync(solutionPathArg);
                Console.WriteLine($"done ({solution.Projects.Count()} projects).");

                // Compute workspace info
                var gitRoot = Path.GetDirectoryName(Path.GetFullPath(solutionPathArg))!;
                Console.Write("Building workspace info... ");
                var workspaceInfo = new WorkspaceInfo(solution, gitRoot);
                Console.WriteLine("done.");

                // Create snapshot
                var snapshotId = SnapshotId.New();
                var manifest = SnapshotManifest.FromWorkspace(workspaceInfo, snapshotId);
                var snapshotIdStr = snapshotId.ToString();

                Console.Write("Saving snapshot to database... ");
                manifest.Save(store, workspaceInfo.DocumentContents, jsonExportPath);
                Console.WriteLine("done.");

                // Index each project compilation
                int totalDeclarations = 0;
                int totalEdges = 0;
                int totalDiagnostics = 0;

                await foreach (var (project, compilation) in CompilationHelper.GetAllAsync(solution))
                {
                    var projectName = project.Name;
                    Console.Write($"  [{projectName}] ");

                    // Extract symbols
                    var extractor = new SymbolExtractor(
                        compilation,
                        workspaceInfo.DocumentContents,
                        workspaceInfo.Documents,
                        workspaceInfo.GeneratedDocuments,
                        snapshotIdStr);
                    var declarations = extractor.ExtractAll();
                    store.SaveDeclarations(snapshotIdStr, declarations);
                    totalDeclarations += declarations.Count;

                    // Extract edges (type-level)
                    var edges = extractor.ExtractEdges();
                    store.SaveEdges(snapshotIdStr, edges);
                    totalEdges += edges.Count;

                    // Extract member-level edges
                    var memberEdgeExtractor = new MemberEdgeExtractor(
                        compilation, workspaceInfo.Documents, workspaceInfo.GeneratedDocuments, snapshotIdStr);
                    var memberEdges = memberEdgeExtractor.ExtractAll();
                    store.SaveEdges(snapshotIdStr, memberEdges);
                    totalEdges += memberEdges.Count;

                    // Extract polymorphism edges (MayDispatchTo)
                    var polyExtractor = new PolymorphismExtractor(
                        compilation, snapshotIdStr);
                    var polyEdges = polyExtractor.ExtractAll();
                    store.SaveEdges(snapshotIdStr, polyEdges);
                    totalEdges += polyEdges.Count;

                    // B6: Extract reflection edges
                    int reflectionEdgesCount = 0;
                    try
                    {
                        var reflectionExtractor = new ReflectionExtractor(
                            compilation, snapshotIdStr);
                        var reflectionEdges = reflectionExtractor.Extract();
                        store.SaveEdges(snapshotIdStr, reflectionEdges);
                        reflectionEdgesCount = reflectionEdges.Count;
                        totalEdges += reflectionEdgesCount;
                        Console.WriteLine($"  Reflection extraction: {reflectionEdgesCount} edges.");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"WARNING: Reflection extraction failed: {ex.Message}");
                    }

                    // B5: Run framework adapters
                    int adapterEdgesCount = 0;
                    var adaptersToRun = Adapters.AdapterRegistry.GetAdapters(skipAdapters);
                    foreach (var adapter in adaptersToRun)
                    {
                        try
                        {
                            Console.Write($"  Running adapter [{adapter.Name}]... ");
                            var adapterEdges = adapter.Extract(compilation, snapshotIdStr);
                            store.SaveEdges(snapshotIdStr, adapterEdges);
                            adapterEdgesCount += adapterEdges.Count;
                            Console.WriteLine($"{adapterEdges.Count} edges.");
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"ERROR: Adapter '{adapter.Name}' failed: {ex.Message}");
                            // continue to next adapter
                        }
                    }

                    // Extract diagnostics
                    var diagnostics = CompilationHelper.GetDiagnostics(projectName, compilation);
                    store.SaveDiagnostics(snapshotIdStr, diagnostics);
                    totalDiagnostics += diagnostics.Count;

                    totalEdges += adapterEdgesCount;

                    Console.WriteLine($"{declarations.Count} symbols, {edges.Count + memberEdges.Count + polyEdges.Count + reflectionEdgesCount + adapterEdgesCount} edges, {diagnostics.Count} diagnostics.");
                }

                Console.WriteLine();
                Console.WriteLine($"Index complete for snapshot {snapshotIdStr}");
                Console.WriteLine($"  Declarations: {totalDeclarations}");
                Console.WriteLine($"  Edges:        {totalEdges}");
                Console.WriteLine($"  Diagnostics:  {totalDiagnostics}");
                Console.WriteLine($"  Schema v{VersionConstants.DatabaseSchemaVersion}");

                // B3: auto-diff against previous snapshot if one exists
                var storageWsId = new Storage.WorkspaceId(manifest.WorkspaceId.Value);
                var previousManifest = store.LoadLatestSnapshot(storageWsId);
                if (previousManifest != null && previousManifest.SnapshotId != snapshotIdStr)
                {
                    Console.WriteLine();
                    Console.Write("Computing semantic diff from previous snapshot... ");
                    var differ = new SemanticDiffer(dbPath, store);
                    var diffChanges = differ.ComputeDiff(previousManifest.SnapshotId, snapshotIdStr);
                    store.SaveSemanticChanges(previousManifest.SnapshotId, snapshotIdStr, diffChanges);
                    Console.WriteLine($"done ({diffChanges.Count} changes).");
                }
            }
            finally
            {
                store.Close();
            }
        }

        private static void TestMigration()
        {
            Console.WriteLine("Testing migration runner...");

            var dbPath = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location)!, "test-index.db");
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }

            var store = new SqliteIndexStore(dbPath);
            store.Open(dbPath);

            var initialVersion = store.GetCurrentSchemaVersion();
            Console.WriteLine($"Initial schema version: {initialVersion}");

            store.RunMigrations();

            var afterVersion = store.GetCurrentSchemaVersion();
            Console.WriteLine($"Schema version after migrations: {afterVersion}");

            store.RunMigrations();

            var secondVersion = store.GetCurrentSchemaVersion();
            Console.WriteLine($"Schema version after second run: {secondVersion}");

            var expected = VersionConstants.DatabaseSchemaVersion;
            if (afterVersion == expected && secondVersion == expected)
            {
                Console.WriteLine($"✓ Migration test passed: schema version is {expected} and idempotent");
            }
            else
            {
                Console.WriteLine($"✗ Migration test failed: expected {expected}, got {afterVersion}/{secondVersion}");
                Environment.Exit(1);
            }

            store.Close();
        }

        private static void RunDiff(string[] args)
        {
            var outputDirArg = args.FirstOrDefault(a => a.StartsWith("--output-dir="))?.Split('=', 2)[1]
                ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
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

            var fromSnapshot = args.FirstOrDefault(a => a.StartsWith("--from-snapshot="))?.Split('=', 2)[1];
            var toSnapshot = args.FirstOrDefault(a => a.StartsWith("--to-snapshot="))?.Split('=', 2)[1];

            if (string.IsNullOrEmpty(fromSnapshot) || string.IsNullOrEmpty(toSnapshot))
            {
                Console.Error.WriteLine("ERROR: --from-snapshot=<id> and --to-snapshot=<id> are required for --mode=diff.");
                Environment.Exit(1);
            }

            var store = new SqliteIndexStore(dbPath);
            store.Open(dbPath);
            try
            {
                var differ = new SemanticDiffer(dbPath, store);
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

        private static void ShowStatus()
        {
            if (_indexStore == null || !_indexStore.IsOpen)
            {
                Console.WriteLine("Index store is not open");
                return;
            }

            var version = _indexStore.GetCurrentSchemaVersion();
            Console.WriteLine($"Database schema version: {version}");
        }
    }
}
