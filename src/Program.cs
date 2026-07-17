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

            if (args.Contains("--mode=impact"))
            {
                RunImpact(args);
                return;
            }

            if (args.Contains("--mode=context"))
            {
                RunContext(args);
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

            Console.Error.WriteLine("ERROR: Unknown mode. Use --mode=index, --mode=get-source, --mode=get-symbol, --mode=search, --mode=find-symbol, --mode=diff, --mode=impact, --mode=context, --mode=status, or --mode=test-migration.");
            Console.Error.WriteLine("  Note: 'structure' is served by --mode=context --intent=inspect.");
            Console.Error.WriteLine("  Note: 'who-references' is served by --mode=impact --direction=upstream.");
            Console.Error.WriteLine("  Note: 'discover' is served by --mode=search --type=symbol --kind=<TypeKind>.");
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
                    var latestSnapshot = _indexStore.GetLatestSnapshotId();
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
                    snapshotId = _indexStore.GetLatestSnapshotId();
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
            var kindArg = args.FirstOrDefault(a => a.StartsWith("--kind="))?.Split('=', 2)[1];
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
                    snapshotId = _indexStore.GetLatestSnapshotId();
                    if (snapshotId == null)
                    {
                        Console.Error.WriteLine("ERROR: No snapshots found in the database.");
                        Environment.Exit(1);
                    }
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
                    var symbolResults = _indexStore.SearchSymbols(queryArg, snapshotId, limit, includeGenerated, kindArg);
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
                    snapshotId = _indexStore.GetLatestSnapshotId();
                    if (snapshotId == null)
                    {
                        Console.Error.WriteLine("ERROR: No snapshots found in the database.");
                        Environment.Exit(1);
                    }
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

            
            var jsonExportPath = args.FirstOrDefault(a => a.StartsWith("--output-json="))?.Split('=', 2)[1];

            
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

            
            if (!MSBuildLocator.IsRegistered)
            {
                var instances = MSBuildLocator.RegisterDefaults();
                Console.WriteLine($"MSBuild: {instances?.MSBuildPath ?? "default"}");
            }

            
            var store = new SqliteIndexStore(dbPath);
            store.Open(dbPath);
            store.RunMigrations();
            store.ValidateSchema(expectedVersion: VersionConstants.DatabaseSchemaVersion);

            try
            {
                
                Console.Write("Loading solution... ");
                using var workspace = MSBuildWorkspace.Create();
                var solution = await workspace.OpenSolutionAsync(solutionPathArg);
                Console.WriteLine($"done ({solution.Projects.Count()} projects).");

                
                var gitRoot = Path.GetDirectoryName(Path.GetFullPath(solutionPathArg))!;
                Console.Write("Building workspace info... ");
                var workspaceInfo = new WorkspaceInfo(solution, gitRoot);
                Console.WriteLine("done.");

                
                var snapshotId = SnapshotId.New();
                var manifest = SnapshotManifest.FromWorkspace(workspaceInfo, snapshotId);
                var snapshotIdStr = snapshotId.ToString();

                Console.Write("Saving snapshot to database... ");
                manifest.Save(store, workspaceInfo.DocumentContents, jsonExportPath);
                Console.WriteLine("done.");

                
                int totalDeclarations = 0;
                int totalEdges = 0;
                int totalDiagnostics = 0;

                await foreach (var (project, compilation) in CompilationHelper.GetAllAsync(solution))
                {
                    var projectName = project.Name;
                    Console.Write($"  [{projectName}] ");

                    
                    var extractor = new SymbolExtractor(
                        compilation,
                        workspaceInfo.DocumentContents,
                        workspaceInfo.Documents,
                        workspaceInfo.GeneratedDocuments,
                        snapshotIdStr);
                    var declarations = extractor.ExtractAll();
                    store.SaveDeclarations(snapshotIdStr, declarations);
                    totalDeclarations += declarations.Count;

                    
                    var edges = extractor.ExtractEdges();
                    store.SaveEdges(snapshotIdStr, edges);
                    totalEdges += edges.Count;

                    
                    var memberEdgeExtractor = new MemberEdgeExtractor(
                        compilation, workspaceInfo.Documents, workspaceInfo.GeneratedDocuments, snapshotIdStr);
                    var memberEdges = memberEdgeExtractor.ExtractAll();
                    store.SaveEdges(snapshotIdStr, memberEdges);
                    totalEdges += memberEdges.Count;

                    
                    var polyExtractor = new PolymorphismExtractor(
                        compilation, snapshotIdStr);
                    var polyEdges = polyExtractor.ExtractAll();
                    store.SaveEdges(snapshotIdStr, polyEdges);
                    totalEdges += polyEdges.Count;

                    
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
                            
                        }
                    }

                    
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

                
                var storageWsId = new Storage.WorkspaceId(manifest.WorkspaceId.Value);
                var previousManifest = store.LoadLatestSnapshot(storageWsId);
                if (previousManifest != null && previousManifest.SnapshotId != snapshotIdStr)
                {
                    Console.WriteLine();
                    Console.Write("Computing semantic diff from previous snapshot... ");
                    var differ = new SemanticDiffer(store);
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
                var differ = new SemanticDiffer(store);
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

        private static void RunImpact(string[] args)
        {
            var symbolArg = args.FirstOrDefault(a => a.StartsWith("--symbol="))?.Split('=', 2)[1];
            if (string.IsNullOrEmpty(symbolArg))
            {
                Console.Error.WriteLine("ERROR: --symbol=<symbol-id> is required for --mode=impact.");
                Environment.Exit(1);
            }

            var directionArg = args.FirstOrDefault(a => a.StartsWith("--direction="))?.Split('=', 2)[1] ?? "downstream";
            ImpactDirection direction;
            switch (directionArg.ToLowerInvariant())
            {
                case "downstream":
                    direction = ImpactDirection.Downstream;
                    break;
                case "upstream":
                    direction = ImpactDirection.Upstream;
                    break;
                default:
                    Console.Error.WriteLine($"ERROR: Invalid direction '{directionArg}'. Use 'upstream' or 'downstream'.");
                    Environment.Exit(1);
                    return;
            }

            var snapshotArg = args.FirstOrDefault(a => a.StartsWith("--snapshot="))?.Split('=', 2)[1];

            var maxDepthArg = args.FirstOrDefault(a => a.StartsWith("--max-depth="))?.Split('=', 2)[1];
            int maxDepth = 10;
            if (!string.IsNullOrEmpty(maxDepthArg))
            {
                if (!int.TryParse(maxDepthArg, out maxDepth) || maxDepth < 1)
                {
                    Console.Error.WriteLine("ERROR: --max-depth must be a positive integer.");
                    Environment.Exit(1);
                }
            }

            var kindsArg = args.FirstOrDefault(a => a.StartsWith("--kinds="))?.Split('=', 2)[1];
            HashSet<string>? allowedKinds = null;
            if (!string.IsNullOrEmpty(kindsArg))
            {
                allowedKinds = new HashSet<string>(
                    kindsArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
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

            var store = new SqliteIndexStore(dbPath);
            store.Open(dbPath);

            try
            {
                var snapshotId = snapshotArg;
                if (string.IsNullOrEmpty(snapshotId))
                {
                    snapshotId = _indexStore.GetLatestSnapshotId();
                    if (snapshotId == null)
                    {
                        Console.Error.WriteLine("ERROR: No snapshots found in the database.");
                        Environment.Exit(1);
                    }
                }

                var traverser = new ImpactTraverser(store, snapshotId);
                var paths = traverser.TraceImpact(
                    symbolId: symbolArg,
                    direction: direction,
                    allowedEdgeKinds: allowedKinds,
                    maxDepth: maxDepth,
                    includeSource: true);

                var json = JsonSerializer.Serialize(new
                {
                    snapshot_id = snapshotId,
                    symbol_id = symbolArg,
                    direction = direction == ImpactDirection.Downstream ? "downstream" : "upstream",
                    max_depth = maxDepth,
                    paths = paths.Select(p => new
                    {
                        truncated = p.Truncated,
                        truncation_reason = p.TruncationReason,
                        total_steps = p.TotalSteps,
                        hops = p.Hops.Select(h => new
                        {
                            source_symbol_id = h.SourceSymbolId,
                            target_symbol_id = h.TargetSymbolId,
                            edge_kind = h.EdgeKind,
                            provenance = h.Provenance,
                            source_document = h.SourceDocument,
                            source_line = h.SourceLine
                        })
                    })
                }, new JsonSerializerOptions { WriteIndented = true });

                Console.WriteLine(json);
            }
            finally
            {
                store.Close();
            }
        }

        private static void RunContext(string[] args)
        {
            var symbolArg = args.FirstOrDefault(a => a.StartsWith("--symbol="))?.Split('=', 2)[1];
            var fileArg = args.FirstOrDefault(a => a.StartsWith("--file="))?.Split('=', 2)[1];
            var lineArg = args.FirstOrDefault(a => a.StartsWith("--line="))?.Split('=', 2)[1];
            var intentArg = args.FirstOrDefault(a => a.StartsWith("--intent="))?.Split('=', 2)[1] ?? "inspect";
            var budgetArg = args.FirstOrDefault(a => a.StartsWith("--budget="))?.Split('=', 2)[1];
            var snapshotArg = args.FirstOrDefault(a => a.StartsWith("--snapshot="))?.Split('=', 2)[1];
            var maxHopsArg = args.FirstOrDefault(a => a.StartsWith("--max-hops="))?.Split('=', 2)[1];
            var includeGenerated = args.Contains("--include-generated");

            var outputDirArg = args.FirstOrDefault(a => a.StartsWith("--output-dir="))?.Split('=', 2)[1]
                ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");

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

            ContextIntent intent;
            switch (intentArg.ToLowerInvariant())
            {
                case "inspect": intent = ContextIntent.Inspect; break;
                case "modify": intent = ContextIntent.Modify; break;
                case "diagnose": intent = ContextIntent.Diagnose; break;
                default:
                    Console.Error.WriteLine("ERROR: --intent must be one of: inspect, modify, diagnose.");
                    Environment.Exit(1);
                    return;
            }

            int budget = 8000;
            if (!string.IsNullOrEmpty(budgetArg) && (!int.TryParse(budgetArg, out budget) || budget < 1))
            {
                Console.Error.WriteLine("ERROR: --budget must be a positive integer.");
                Environment.Exit(1);
            }

            int maxHops = 3;
            if (!string.IsNullOrEmpty(maxHopsArg) && (!int.TryParse(maxHopsArg, out maxHops) || maxHops < 1))
            {
                Console.Error.WriteLine("ERROR: --max-hops must be a positive integer.");
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
                    snapshotId = _indexStore.GetLatestSnapshotId();
                    if (snapshotId == null)
                    {
                        Console.Error.WriteLine("ERROR: No snapshots found in the database.");
                        Environment.Exit(1);
                    }
                }

                if (hasSymbol)
                {
                    var symbolId = SymbolId.Parse(symbolArg!);
                    var assembler = new ContextAssembler(
                        store: store,
                        snapshotId: snapshotId,
                        symbolId: symbolId,
                        intent: intent,
                        budget: budget,
                        maxHops: maxHops,
                        includeGenerated: includeGenerated);
                    var capsule = assembler.Assemble();
                    WriteCapsuleOutput(capsule, outputDirArg);
                }
                else
                {
                    if (!int.TryParse(lineArg, out var lineNumber) || lineNumber < 1)
                    {
                        Console.Error.WriteLine("ERROR: --line must be a positive integer.");
                        Environment.Exit(1);
                    }

                    var resolvedId = store.ResolveSymbolByLocation(fileArg!, lineNumber, snapshotId, includeGenerated);

                    if (resolvedId == null)
                    {
                        var gapAnchor = new CapsuleAnchor(
                            symbolId: $"file://{fileArg}:{lineNumber}",
                            fullyQualifiedName: $"<no symbol at {fileArg}:{lineNumber}>",
                            kind: "gap",
                            source: string.Empty);

                        var gapCapsule = new ContextCapsule(gapAnchor)
                        {
                            Budget = budget,
                            EstimatedTokens = 0,
                            Truncated = false,
                        };

                        gapCapsule.Uncertainties.Add(new UncertaintyEntry(
                            new List<string> { gapAnchor.SymbolId },
                            "location_gap",
                            $"No symbol found at {fileArg}:{lineNumber}. The location may be in a comment, whitespace, or within a region not represented in the index."));

                        WriteCapsuleOutput(gapCapsule, outputDirArg);
                    }
                    else
                    {
                        var symbolId = SymbolId.Parse(resolvedId);
                        var assembler = new ContextAssembler(
                            store: store,
                            snapshotId: snapshotId,
                            symbolId: symbolId,
                            intent: intent,
                            budget: budget,
                            maxHops: maxHops,
                            includeGenerated: includeGenerated);
                        var capsule = assembler.Assemble();
                        WriteCapsuleOutput(capsule, outputDirArg);
                    }
                }
            }
            finally
            {
                store.Close();
            }
        }

        private static void WriteCapsuleOutput(ContextCapsule capsule, string outputDirArg)
        {
            var json = JsonSerializer.Serialize(capsule, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

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
