using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;

using RoslynIndexer;

namespace RoslynIndexer;

record DirtyManifest(int SchemaVersion, List<string> DirtyFiles, List<string> DeletedFiles, string MarkedAt);
record DeclarationSite(string? File, int Line);
record ReferenceDetail(string? File, int Line, string Project, string? ContainingSymbol, string LocationProvenance, string Kind, string KindProvenance);
record DiscoveredType(string SymbolId, string Kind, string KindCategory, string Namespace, string Project,
    string SourceFile, string Accessibility, string? Inherits, List<string> Implements, List<string> Dependencies);
record FanSummary(int Count, List<string> Sources, Dictionary<string, int> ByProject);

record ProjectCatalog(string Name, string AssemblyName, string ProjectPath, string OutputPath);

record SymbolRecord(
    string MetadataName,
    string Kind,
    string KindRule,
    string KindProvenance,
    string Project,
    string File,
    int Line,
    string Namespace,
    bool IsAbstract,
    bool IsSealed,
    int MemberCount,
    List<string> OutgoingTypeNames
);

    record DiscoverySnapshot(
        List<ProjectCatalog> Projects,
        List<SymbolRecord> Symbols,
        Dictionary<string, List<string>> IncomingEdges,
        DateTime BuiltAtUtc
    );

    record DiagnosticBaseline(int SchemaVersion, string CapturedAtUtc, string SolutionPath, List<DiagnosticEntry> Diagnostics);
    record DiagnosticEntry(string ProjectName, string Id, string Severity, string Message, string File, int Line);

    record ResolveCandidate(string MetadataName, string Kind, string Project, string File, int Line, int Score, string ScoreReason);

public class Program
{


    private const string ProvenanceCompilerProved = "compiler_proved";
    private const string ProvenanceIndexerObserved = "indexer_observed";
    private const string ProvenanceCacheSuggests = "cache_suggests";
    private const string ProvenanceNotDeterminable = "not_determinable";

    private const int HighIncomingDefaultThreshold = 5;
    private const int CrossProjectMinCount = 2;
    private const int HighOutgoingMinCount = 10;

    private static bool _useJson;

    private static string GitRoot = null!;
    private static string CodeAuditDir = null!;
    private static string SemanticDir = null!;
    private static string DirtyFilePath = null!;

    private static string SolutionPath = null!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task Main(string[] args)
    {
        _useJson = args.Contains("--json");

        var solutionPathArg = args.FirstOrDefault(a => a.StartsWith("--solution="))?.Split('=', 2)[1]
            ?? Environment.GetEnvironmentVariable("INDEXER_SOLUTION_PATH");
        if (string.IsNullOrEmpty(solutionPathArg) || !File.Exists(solutionPathArg))
        {
            Console.Error.WriteLine("ERROR: --solution=path or INDEXER_SOLUTION_PATH is required and must point to an existing .sln file.");
            Environment.Exit(1);
        }
        SolutionPath = Path.GetFullPath(solutionPathArg);
        GitRoot = ResolveGitRoot(Path.GetDirectoryName(SolutionPath)!);

        var outputDirArg = args.FirstOrDefault(a => a.StartsWith("--output-dir="))?.Split('=', 2)[1]
            ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
        if (string.IsNullOrEmpty(outputDirArg))
        {
            Console.Error.WriteLine("ERROR: --output-dir=path or INDEXER_OUTPUT_DIR is required.");
            Environment.Exit(1);
        }
        var outputDir = Path.GetFullPath(outputDirArg);
        CodeAuditDir = outputDir;
        SemanticDir = Path.Combine(CodeAuditDir, "semantic");
        DirtyFilePath = Path.Combine(CodeAuditDir, "dirty-files.json");

        var gitRootNormalized = GitRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!SolutionPath.StartsWith(gitRootNormalized, StringComparison.OrdinalIgnoreCase))
            Console.Error.WriteLine($"WARNING: solution path '{SolutionPath}' is not under git root '{GitRoot}' — relative paths may be incorrect.");

        EnsureDirectories();

        var mode = args.FirstOrDefault(a => a.StartsWith("--mode="))?.Split('=', 2)[1] ?? "help";

        try
        {
            switch (mode)
            {
                case "fingerprint":
                    var fpSymbol = args.FirstOrDefault(a => a.StartsWith("--symbol="))?.Split('=', 2)[1];
                    if (string.IsNullOrEmpty(fpSymbol))
                    {
                        if (_useJson)
                            WriteJsonResult("fingerprint", new { error = "Missing required argument --symbol=Namespace.ClassName" });
                        else
                            Console.WriteLine("Usage: --mode=fingerprint --symbol=Namespace.ClassName");
                    }
                    else
                    {
                        await ComputeFingerprintOnlyAsync(fpSymbol);
                    }
                    break;

                case "who-references":
                    var wrSymbol = args.FirstOrDefault(a => a.StartsWith("--symbol="))?.Split('=', 2)[1];
                    if (string.IsNullOrEmpty(wrSymbol))
                    {
                        if (_useJson)
                            WriteJsonResult("who-references", new { error = "Missing required argument --symbol=Namespace.ClassName" });
                        else
                            Console.WriteLine("Usage: --mode=who-references --symbol=Namespace.ClassName [--json]");
                    }
                    else
                    {
                        await WhoReferencesAsync(wrSymbol);
                    }
                    break;

                case "recompute-all":
                    await RecomputeAllFingerprintsAsync();
                    break;

                case "mark-dirty":
                    var markResult = await MarkDirtyAsync(args);
                    if (_useJson)
                        WriteJsonResult("mark-dirty", markResult);
                    else
                        Console.WriteLine("Manifest updated.");
                    break;

                case "sweep":
                    await SweepAsync();
                    break;

                case "status":
                    await ShowStatusAsync();
                    break;

                case "lint":
                    await LintModeAsync();
                    break;

                case "impact":
                    await ImpactModeAsync();
                    break;

                case "discover":
                    await DiscoverAsync(args);
                    break;

                case "structure":
                    await StructureAsync(args);
                    break;
                case "verify-facts":
                    await VerifyFactsAsync();
                    break;
                case "audit":
                    await AuditModeAsync(args);
                    break;

                case "resolve":
                    var resolveSymbol = args.FirstOrDefault(a => a.StartsWith("--symbol="))?.Split('=', 2)[1];
                    if (string.IsNullOrEmpty(resolveSymbol))
                    {
                        if (_useJson)
                            WriteJsonResult("resolve", new { error = "Missing required argument --symbol=<partial>" });
                        else
                            Console.WriteLine("Usage: --mode=resolve --symbol=<partial> [--json]");
                    }
                    else
                    {
                        var solution = await LoadSolutionAsync();
                        var candidates = await ResolveSymbolAsync(solution, resolveSymbol);
                        if (_useJson)
                        {
                            WriteJsonResult("resolve", new
                            {
                                query = resolveSymbol,
                                totalCandidates = candidates.Count,
                                candidates
                            });
                        }
                        else
                        {
                            Console.WriteLine($"\nResolved {candidates.Count} candidate(s) for '{resolveSymbol}':");
                            foreach (var c in candidates)
                                Console.WriteLine($"  [{c.Score,3}] {c.MetadataName} ({c.Kind}) in {c.File}:{c.Line}");
                        }
                    }
                    break;

                case "check":
                    {
                        var checkSolution = await LoadSolutionAsync();
                        var baselinePath = Path.Combine(CodeAuditDir, "baseline-diagnostics.json");
                        var collectedDiags = await SaveBaselineAsync(checkSolution, baselinePath);
                        var severityCounts = collectedDiags
                            .GroupBy(d => d.Severity)
                            .ToDictionary(g => g.Key, g => g.Count());
                        var relBaselinePath = GetRelativePath(baselinePath);
                        if (_useJson)
                        {
                            WriteJsonResult("check", new
                            {
                                baselinePath = relBaselinePath,
                                projectCount = checkSolution.Projects.Count(),
                                diagnosticCount = collectedDiags.Count,
                                severityCounts
                            });
                        }
                        else
                        {
                            Console.WriteLine($"Baseline saved: {collectedDiags.Count} diagnostic(s) across {checkSolution.Projects.Count()} project(s).");
                            Console.WriteLine($"  Path: {relBaselinePath}");
                            foreach (var kv in severityCounts.OrderBy(k => k.Key))
                                Console.WriteLine($"  {kv.Key}: {kv.Value}");
                        }
                    }
                    break;

                case "simulate-delete":
                    var sdSymbol = args.FirstOrDefault(a => a.StartsWith("--symbol="))?.Split('=', 2)[1];
                    if (string.IsNullOrEmpty(sdSymbol))
                    {
                        if (_useJson)
                            WriteJsonResult("simulate-delete", new { error = "Missing required argument --symbol=<FQN>" });
                        else
                            Console.WriteLine("Usage: --mode=simulate-delete --symbol=<FQN> [--json]");
                    }
                    else
                    {
                        await HandleSimulateDeleteAsync(sdSymbol);
                    }
                    break;

                case "simulate-rename":
                    var srSymbol = args.FirstOrDefault(a => a.StartsWith("--symbol="))?.Split('=', 2)[1];
                    var newName = args.FirstOrDefault(a => a.StartsWith("--new-name="))?.Split('=', 2)[1];
                    if (string.IsNullOrEmpty(srSymbol) || string.IsNullOrEmpty(newName))
                    {
                        if (_useJson)
                            WriteJsonResult("simulate-rename", new { error = "Missing required arguments --symbol=<FQN> --new-name=<SimpleName>" });
                        else
                            Console.WriteLine("Usage: --mode=simulate-rename --symbol=<FQN> --new-name=<SimpleName> [--json]");
                    }
                    else
                    {
                        await HandleSimulateRenameAsync(srSymbol, newName);
                    }
                    break;

                case "simulate-move":
                    var smSymbol = args.FirstOrDefault(a => a.StartsWith("--symbol="))?.Split('=', 2)[1];
                    var smTargetNamespace = args.FirstOrDefault(a => a.StartsWith("--target-namespace="))?.Split('=', 2)[1];
                    var smTargetProject = args.FirstOrDefault(a => a.StartsWith("--target-project="))?.Split('=', 2)[1];
                    if (string.IsNullOrEmpty(smSymbol) || string.IsNullOrEmpty(smTargetNamespace))
                    {
                        if (_useJson)
                            WriteJsonResult("simulate-move", new { error = "Missing required arguments --symbol=<FQN> --target-namespace=<Namespace>" });
                        else
                            Console.WriteLine("Usage: --mode=simulate-move --symbol=<FQN> --target-namespace=<Namespace> [--target-project=<ProjectName>] [--json]");
                    }
                    else
                    {
                        await HandleSimulateMoveAsync(smSymbol, smTargetNamespace, smTargetProject);
                    }
                    break;

                case "prune":
                    await HandlePruneAsync();
                    break;

                default:
                    Console.WriteLine("Required arguments:");
                    Console.WriteLine("  --solution=PATH      Path to the .sln file (or INDEXER_SOLUTION_PATH env var)");
                    Console.WriteLine("  --output-dir=PATH    Output directory for .codeaudit artifacts (or INDEXER_OUTPUT_DIR env var)");
                    Console.WriteLine();
                    Console.WriteLine("Commands:");
                    Console.WriteLine("  --mode=discover [--kind=X] [--project=X] [--json]");
                    Console.WriteLine("  --mode=structure --symbol=X [--depth=2] [--json]");
                    Console.WriteLine("  --mode=fingerprint --symbol=X");
                    Console.WriteLine("  --mode=who-references --symbol=X [--json]");
                    Console.WriteLine("  --mode=recompute-all");
                    Console.WriteLine("  --mode=mark-dirty --files=PATH [--deleted=PATH]");
                    Console.WriteLine("  --mode=sweep");
                    Console.WriteLine("  --mode=status");
                    Console.WriteLine("  --mode=lint");
                    Console.WriteLine("  --mode=impact [--json]  (annotates affected entries with impact tiers when snapshot available)");
                    Console.WriteLine("  --mode=verify-facts [--json]");
                    Console.WriteLine("  --mode=resolve --symbol=<partial> [--json]");
                    Console.WriteLine("  --mode=check [--json]");
                    Console.WriteLine("  --mode=simulate-delete --symbol=<FQN> [--json]");
                    Console.WriteLine("  --mode=simulate-rename --symbol=<FQN> --new-name=<SimpleName> [--json]");
                    Console.WriteLine("  --mode=simulate-move --symbol=<FQN> --target-namespace=<Namespace> [--target-project=<ProjectName>] [--json]");
                    Console.WriteLine("  --mode=prune [--json]");
                    Console.WriteLine("  --mode=audit --check=dead-code|complexity-candidates|depth|redundancy|all [--project=<name>] [--min-incoming=N] [--json]");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex}");
            Environment.Exit(1);
        }
    }

    private static void EnsureDirectories()
    {
        Directory.CreateDirectory(CodeAuditDir);
        Directory.CreateDirectory(SemanticDir);
    }

    private static string ResolveGitRoot(string startPath)
    {
        var dir = new DirectoryInfo(startPath);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate repository root (.git directory) from " + startPath);
    }

    private static void WriteProgress(string message)
    {
        if (_useJson)
            Console.Error.WriteLine(message);
        else
            Console.WriteLine(message);
    }

    private static void WriteJsonResult(string command, object? result)
    {
        var envelope = new
        {
            indexerVersion = VersionConstants.ToolVersion,
            schemaVersion = VersionConstants.OutputSchemaVersion,
            command,
            solutionPath = SolutionPath,
            timestampUtc = DateTime.UtcNow.ToString("O"),
            result
        };
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        Console.WriteLine(json);
    }

    private static async Task AuditModeAsync(string[] args)
    {
        var checkArg = args.FirstOrDefault(a => a.StartsWith("--check="))?.Split('=', 2)[1];
        var projectArg = args.FirstOrDefault(a => a.StartsWith("--project="))?.Split('=', 2)[1];
        var minIncomingArg = args.FirstOrDefault(a => a.StartsWith("--min-incoming="))?.Split('=', 2)[1];

        if (string.IsNullOrEmpty(checkArg))
        {
            Console.Error.WriteLine("ERROR: --check=<name> is required for --mode=audit.");
            Environment.Exit(1);
            return;
        }

        var validChecks = new[] { "dead-code", "complexity-candidates", "depth", "redundancy", "all" };
        if (!validChecks.Contains(checkArg))
        {
            Console.Error.WriteLine($"ERROR: Invalid --check value '{checkArg}'. Valid values are: {string.Join(", ", validChecks)}");
            Environment.Exit(1);
            return;
        }

        int minIncoming = 0;
        if (!string.IsNullOrEmpty(minIncomingArg) && !int.TryParse(minIncomingArg, out minIncoming))
        {
            Console.Error.WriteLine($"ERROR: Invalid --min-incoming value '{minIncomingArg}'. Must be an integer.");
            Environment.Exit(1);
            return;
        }

        Solution solution;
        try
        {
            solution = await LoadSolutionAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Failed to load solution: {ex.Message}");
            Environment.Exit(1);
            return;
        }

        var snapshot = await BuildDiscoverySnapshotAsync(solution);

        object resultObject;
        switch (checkArg)
        {
            case "dead-code":
                var deadCodeFindings = await DeadCodeCheckAsync(snapshot, projectArg);
                var deadBucket = deadCodeFindings.Count(f =>
                    ((dynamic)f).bucket == "dead");
                var testOnlyBucket = deadCodeFindings.Count(f =>
                    ((dynamic)f).bucket == "test_only");
                var notDeterminableBucket = deadCodeFindings.Count(f =>
                    ((dynamic)f).bucket == "not_determinable");
                resultObject = new
                {
                    check = "dead-code",
                    totalFindings = deadCodeFindings.Count,
                    bucketCounts = new
                    {
                        dead = deadBucket,
                        test_only = testOnlyBucket,
                        not_determinable = notDeterminableBucket
                    },
                    findings = deadCodeFindings
                };
                break;
            case "complexity-candidates":
                var (complexityFindings, complexityWritten, complexitySkipped, complexitySideEffects) =
                    await ComplexityCandidatesCheckAsync(snapshot, minIncoming);
                resultObject = new
                {
                    check = "complexity-candidates",
                    totalFindings = complexityFindings.Count,
                    bucketCounts = new { },
                    findings = complexityFindings,
                    scaffoldsWritten = complexityWritten,
                    scaffoldsSkipped = complexitySkipped,
                    sideEffects = complexitySideEffects
                };
                break;
            case "depth":
                var depthFindings = DepthCheckAsync(snapshot);
                resultObject = new
                {
                    check = "depth",
                    totalFindings = depthFindings.Count,
                    bucketCounts = new { },
                    findings = depthFindings
                };
                break;
            case "redundancy":
                var redundancyFindings = RedundancyCheckAsync(snapshot);
                resultObject = new
                {
                    check = "redundancy",
                    totalFindings = redundancyFindings.Count,
                    bucketCounts = new { },
                    findings = redundancyFindings
                };
                break;
            case "all":
                var allDeadCode = await DeadCodeCheckAsync(snapshot, projectArg);
                var (allComplexity, allScaffoldsWritten, allScaffoldsSkipped, allSideEffects) =
                    await ComplexityCandidatesCheckAsync(snapshot, minIncoming);
                var allDepth = DepthCheckAsync(snapshot);
                var allRedundancy = RedundancyCheckAsync(snapshot);

                var mergedFindings = new List<object>();

                foreach (var f in allDeadCode)
                {
                    var obj = JsonNode.Parse(JsonSerializer.Serialize(f, JsonOptions))!.AsObject();
                    obj["check"] = "dead-code";
                    mergedFindings.Add(obj);
                }

                foreach (var f in allComplexity)
                {
                    var obj = JsonNode.Parse(JsonSerializer.Serialize(f, JsonOptions))!.AsObject();
                    obj["check"] = "complexity-candidates";
                    mergedFindings.Add(obj);
                }

                foreach (var f in allDepth)
                {
                    var obj = JsonNode.Parse(JsonSerializer.Serialize(f, JsonOptions))!.AsObject();
                    obj["check"] = "depth";
                    mergedFindings.Add(obj);
                }

                foreach (var f in allRedundancy)
                {
                    var obj = JsonNode.Parse(JsonSerializer.Serialize(f, JsonOptions))!.AsObject();
                    obj["check"] = "redundancy";
                    mergedFindings.Add(obj);
                }

                resultObject = new
                {
                    check = "all",
                    totalFindings = mergedFindings.Count,
                    bucketCounts = new { },
                    findings = mergedFindings,
                    scaffoldsWritten = allScaffoldsWritten,
                    scaffoldsSkipped = allScaffoldsSkipped,
                    sideEffects = allSideEffects
                };
                break;
            default:
                resultObject = new
                {
                    check = checkArg,
                    totalFindings = 0,
                    bucketCounts = new { },
                    findings = new List<object>()
                };
                break;
        }

        WriteJsonResult("audit", resultObject);
    }

    private static Dictionary<string, string> GetSymbolToProjectMap(DiscoverySnapshot snapshot)
    {
        return snapshot.Symbols.ToDictionary(s => s.MetadataName, s => s.Project);
    }

    private static async Task<List<object>> DeadCodeCheckAsync(DiscoverySnapshot snapshot, string? projectFilter)
    {
        var findings = new List<object>();

        var fqnToProject = GetSymbolToProjectMap(snapshot);

        foreach (var symbol in snapshot.Symbols)
        {
            var fqn = symbol.MetadataName;
            var (incomingList, incomingCount) = GetIncomingEdges(snapshot, fqn);

            string? bucket = null;

            var isController = symbol.Kind == "controller" && symbol.KindProvenance == "compiler_proved";
            var isMigration = symbol.File.Contains("Migrations/", StringComparison.OrdinalIgnoreCase);
            var isHostedService = symbol.MetadataName.Contains("HostedService") || symbol.MetadataName.Contains("BackgroundService");

            if ((isController || isMigration || isHostedService) && incomingCount == 0)
            {
                bucket = "not_determinable";
            }

            else if (incomingCount > 0)
            {
                var sourceProjects = incomingList!
                    .Distinct()
                    .Select(srcFqn => fqnToProject.TryGetValue(srcFqn, out var proj) ? proj : null)
                    .Where(p => p != null)
                    .Cast<string>()
                    .ToList();

                if (sourceProjects.Count > 0 && sourceProjects.All(p =>
                    p.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Tests", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Spec", StringComparison.OrdinalIgnoreCase)))
                {
                    bucket = "test_only";
                }
            }

            else if (incomingCount == 0)
            {
                bucket = "dead";
            }

            if (bucket == null) continue;

            if (projectFilter != null && !string.Equals(symbol.Project, projectFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            findings.Add(new
            {
                symbol = fqn,
                kind = symbol.Kind,
                kindRule = symbol.KindRule,
                kindProvenance = symbol.KindProvenance,
                project = symbol.Project,
                file = symbol.File,
                line = symbol.Line,
                bucket,
                incomingTypeDependencyCount = incomingCount,
                incomingTypeDependencyProvenance = "indexer_observed",
                blindSpots = GetBlindSpots()
            });
        }

        return findings;
    }

    private static async Task<(List<object> findings, int scaffoldsWritten, int scaffoldsSkipped, List<string> sideEffects)>
        ComplexityCandidatesCheckAsync(DiscoverySnapshot snapshot, int minIncoming)
    {
        var findings = new List<object>();
        var scaffoldsWritten = 0;
        var scaffoldsSkipped = 0;
        var sideEffects = new List<string>();

        var fqnToProject = GetSymbolToProjectMap(snapshot);

        var highIncomingThreshold = Math.Max(HighIncomingDefaultThreshold, minIncoming);

        foreach (var symbol in snapshot.Symbols)
        {
            var fqn = symbol.MetadataName;

            var (incomingList, incomingCount) = GetIncomingEdges(snapshot, fqn);
            var referencingProjectCount = GetReferencingProjectCount(incomingList, fqnToProject);

            var outgoingCount = symbol.OutgoingTypeNames.Count;

            var complexityRules = new List<string>();

            if (incomingCount >= highIncomingThreshold)
                complexityRules.Add("high_incoming");

            if (referencingProjectCount >= CrossProjectMinCount)
                complexityRules.Add("cross_project");

            if (outgoingCount >= HighOutgoingMinCount)
                complexityRules.Add("high_outgoing");

            if (symbol.Kind == "controller" && symbol.KindProvenance == "compiler_proved")
                complexityRules.Add("public_surface");

            if (complexityRules.Count == 0)
                continue;

            var simpleName = fqn.Split('.').Last();
            var scaffoldFileName = $"{SanitizeId(simpleName)}-scaffold.json";
            var scaffoldFullPath = Path.Combine(CodeAuditDir, scaffoldFileName);

            bool shouldWrite = false;
            bool exists = File.Exists(scaffoldFullPath);

            if (exists)
            {
                try
                {
                    var existingText = await File.ReadAllTextAsync(scaffoldFullPath);
                    var existingNode = JsonNode.Parse(existingText);
                    var existingStatus = existingNode?["status"]?.GetValue<string>();

                    if (existingStatus == "scaffold")
                    {
                        shouldWrite = true;
                        scaffoldsWritten++;
                    }
                    else
                    {
                        scaffoldsSkipped++;
                    }
                }
                catch
                {
                    shouldWrite = true;
                    scaffoldsWritten++;
                }
            }
            else
            {
                shouldWrite = true;
                scaffoldsWritten++;
            }

            if (shouldWrite)
            {
                var scaffold = new
                {
                    status = "scaffold",
                    symbol = fqn,
                    kind = symbol.Kind,
                    kindRule = symbol.KindRule,
                    kindProvenance = symbol.KindProvenance,
                    project = symbol.Project,
                    file = symbol.File,
                    line = symbol.Line,
                    incomingTypeDependencyCount = incomingCount,
                    referencingProjectCount,
                    outgoingTypeDependencyCount = outgoingCount,
                    complexityRules,
                    generatedAtUtc = DateTime.UtcNow.ToString("O")
                };

                var scaffoldJson = JsonSerializer.Serialize(scaffold, JsonOptions);
                await AtomicWriteAsync(scaffoldFullPath, scaffoldJson);

                sideEffects.Add(GetRelativePath(scaffoldFullPath));
            }

            findings.Add(new
            {
                symbol = fqn,
                kind = symbol.Kind,
                kindProvenance = symbol.KindProvenance,
                project = symbol.Project,
                file = symbol.File,
                line = symbol.Line,
                incomingTypeDependencyCount = incomingCount,
                referencingProjectCount,
                outgoingTypeDependencyCount = outgoingCount,
                complexityRules,
                scaffoldPath = GetRelativePath(scaffoldFullPath),
                scaffoldWritten = shouldWrite
            });
        }

        return (findings, scaffoldsWritten, scaffoldsSkipped, sideEffects);
    }

    private static List<object> DepthCheckAsync(DiscoverySnapshot snapshot)
    {
        var findings = new List<object>();

        var fqnToSymbol = snapshot.Symbols.ToDictionary(s => s.MetadataName, s => s);

        var incomingCount = new Dictionary<string, int>();
        foreach (var s in snapshot.Symbols)
        {
            var fqn = s.MetadataName;
            var (_, incomingCountValue) = GetIncomingEdges(snapshot, fqn);
            incomingCount[fqn] = incomingCountValue;
        }

        var allChains = new List<(List<string> nodes, bool cycleDetected)>();

        foreach (var symbol in snapshot.Symbols)
        {
            var fqn = symbol.MetadataName;

            if (incomingCount[fqn] == 1)
                continue;

            var rootSymbol = fqnToSymbol[fqn];

            foreach (var nextFqn in rootSymbol.OutgoingTypeNames)
            {

                if (!fqnToSymbol.TryGetValue(nextFqn, out _))
                    continue;
                if (incomingCount[nextFqn] != 1)
                    continue;

                var chain = new List<string> { fqn, nextFqn };
                var visited = new HashSet<string> { fqn, nextFqn };
                WalkDepthChain(chain, visited, nextFqn, fqnToSymbol, incomingCount, allChains);
            }
        }

        allChains = allChains
            .OrderByDescending(c => c.nodes.Count)
            .ToList();

        var deduped = new List<(List<string> nodes, bool cycleDetected)>();
        foreach (var candidate in allChains)
        {
            var isSuffix = deduped.Any(accepted => IsStrictSuffix(candidate.nodes, accepted.nodes));
            if (!isSuffix)
                deduped.Add(candidate);
        }

        var depthBlindSpots = new List<object>
        {
            new { vector = "interface_polymorphism", determinability = "not_determinable" },
            new { vector = "reflection",             determinability = "not_determinable" }
        };

        foreach (var (nodes, cycleDetected) in deduped)
        {
            var chainItems = nodes.Select(fq =>
            {
                var sym = fqnToSymbol[fq];
                return new
                {
                    symbol = fq,
                    project = sym.Project,
                    incomingTypeDependencyCount = incomingCount[fq]
                };
            }).ToList();

            findings.Add(new
            {
                chainRoot = nodes[0],
                chainLength = nodes.Count,
                cycleDetected,
                chain = chainItems,
                provenance = "indexer_observed",
                blindSpots = depthBlindSpots
            });
        }

        return findings;
    }

    private static void WalkDepthChain(
        List<string> chain,
        HashSet<string> visited,
        string currentFqn,
        Dictionary<string, SymbolRecord> fqnToSymbol,
        Dictionary<string, int> incomingCount,
        List<(List<string> nodes, bool cycleDetected)> results)
    {
        var currentSymbol = fqnToSymbol[currentFqn];
        bool foundAny = false;

        foreach (var nextFqn in currentSymbol.OutgoingTypeNames)
        {
            if (!fqnToSymbol.TryGetValue(nextFqn, out _))
                continue;
            if (incomingCount[nextFqn] != 1)
                continue;

            foundAny = true;

            if (visited.Contains(nextFqn))
            {

                var cycleChain = new List<string>(chain) { nextFqn };
                if (cycleChain.Count >= 3)
                    results.Add((cycleChain, true));
                continue;
            }

            visited.Add(nextFqn);
            chain.Add(nextFqn);

            if (chain.Count >= 10)
            {

                if (chain.Count >= 3)
                    results.Add((new List<string>(chain), false));
            }
            else
            {
                WalkDepthChain(chain, visited, nextFqn, fqnToSymbol, incomingCount, results);
            }

            chain.RemoveAt(chain.Count - 1);
            visited.Remove(nextFqn);
        }

        if (!foundAny && chain.Count >= 3)
            results.Add((new List<string>(chain), false));
    }

    private static bool IsStrictSuffix(List<string> candidate, List<string> accepted)
    {
        if (candidate.Count >= accepted.Count)
            return false;
        var skip = accepted.Count - candidate.Count;
        return accepted.Skip(skip).SequenceEqual(candidate);
    }

    private static List<object> RedundancyCheckAsync(DiscoverySnapshot snapshot)
    {
        var findings = new List<object>();

        var redundancyBlindSpots = new List<object>
        {
            new { vector = "interface_polymorphism", determinability = "not_determinable" }
        };

        var kindGroups = snapshot.Symbols
            .GroupBy(s => s.Kind)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.MetadataName).ToList());

        foreach (var kvp in kindGroups)
        {
            var kind = kvp.Key;
            var group = kvp.Value;

            for (int i = 0; i < group.Count; i++)
            {
                var symA = group[i];
                var surfaceA = new HashSet<string>(symA.OutgoingTypeNames);

                for (int j = i + 1; j < group.Count; j++)
                {
                    var symB = group[j];
                    var surfaceB = new HashSet<string>(symB.OutgoingTypeNames);

                    var sharedCount = surfaceA.Intersect(surfaceB).Count();
                    var totalCount = surfaceA.Union(surfaceB).Count();

                    if (totalCount == 0)
                        continue;

                    var similarity = (double)sharedCount / totalCount;

                    if (similarity < 0.80)
                        continue;

                    findings.Add(new
                    {
                        typeA = symA.MetadataName,
                        typeB = symB.MetadataName,
                        kind,
                        similarityScore = Math.Round(similarity, 2),
                        sharedSurfaceCount = sharedCount,
                        totalSurfaceCount = totalCount,
                        similarityProvenance = "indexer_observed",
                        blindSpots = redundancyBlindSpots
                    });
                }
            }
        }

        return findings;
    }

    private static string SanitizeId(string symbolId) =>
        symbolId.Replace(".", "_").Replace("<", "_").Replace(">", "_").Replace(", ", "_").Replace(",", "_");

    private static string GetRelativePath(string? fullPath)
    {
        if (fullPath == null) return "";
        var gitRoot = GitRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetRelativePath(gitRoot, fullPath).Replace('\\', '/');
    }

    private static async Task<Solution> LoadSolutionAsync()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            var sdkInstance = MSBuildLocator
                .QueryVisualStudioInstances(new VisualStudioInstanceQueryOptions
                {
                    DiscoveryTypes = DiscoveryType.DotNetSdk
                })
                .OrderByDescending(i => i.Version)
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "No .NET SDK MSBuild found. Ensure the .NET SDK is installed and 'dotnet' is on PATH.");

            MSBuildLocator.RegisterInstance(sdkInstance);
        }

        var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                Console.Error.WriteLine($"Load warning: {e.Diagnostic.Message}");
        };

        var solution = await workspace.OpenSolutionAsync(SolutionPath);

        var failures = workspace.Diagnostics
            .Where(d => d.Kind == WorkspaceDiagnosticKind.Failure)
            .ToList();

        if (failures.Any())
            throw new InvalidOperationException(
                $"Solution load failed ({failures.Count} errors):\n" +
                string.Join("\n", failures.Select(f => f.Message))
            );

        return solution;
    }

    private static async Task<(INamedTypeSymbol? Type, Compilation? Compilation)> FindTypeSymbolAsync(Solution solution, string name)
    {
        await foreach (var (_, compilation) in CompilationHelper.GetAllAsync(solution))
        {
            var type = compilation.GetTypeByMetadataName(name);
            if (type != null) return (type, compilation);
        }
        return (null, null);
    }

    // These hash generation methods intentionally duplicate traversal logic because their inclusion rules differ:
    // - ComputeFingerprint: Includes ALL members (private, protected, etc.) for complete type representation
    // - ComputeSurfaceHash: Excludes private/protected-and-friend members for surface-level API exposure
    // - ComputeDependencyHash: Focuses only on dependencies, not type structure
    // Hash behavior is part of the semantic-cache trust contract - changes would break cache consistency
    private static string ComputeFingerprint(ISymbol symbol, Compilation compilation)
    {
        var parts = new List<string>();

        if (symbol is INamedTypeSymbol namedType)
        {
            parts.Add($"type:{namedType.ToDisplayString()}");
            parts.Add($"access:{namedType.DeclaredAccessibility}");
            parts.Add($"kind:{namedType.TypeKind}");
            parts.Add($"ns:{namedType.ContainingNamespace?.ToDisplayString() ?? ""}");

            if (namedType.BaseType != null && namedType.BaseType.SpecialType != SpecialType.System_Object)
                parts.Add($"base:{ResolveTypeName(namedType.BaseType)}");

            parts.AddRange(namedType.Interfaces
                .Select(i => $"iface:{ResolveTypeName(i)}")
                .OrderBy(x => x));

            parts.AddRange(namedType.TypeParameters
                .Select(FormatTypeParameter)
                .OrderBy(x => x));

            parts.AddRange(namedType.GetMembers()
                .OfType<INamedTypeSymbol>()
                .Select(t => $"nested:{ResolveTypeName(t)}")
                .OrderBy(x => x));

            parts.AddRange(namedType.GetMembers()
                .OfType<IMethodSymbol>()
                .Where(m => m.MethodKind == MethodKind.Constructor)
                .Select(FormatConstructor)
                .OrderBy(x => x));

            parts.AddRange(namedType.GetMembers()
                .OfType<IFieldSymbol>()
                .Select(FormatField)
                .OrderBy(x => x));

            parts.AddRange(namedType.GetMembers()
                .OfType<IPropertySymbol>()
                .Select(FormatProperty)
                .OrderBy(x => x));

            parts.AddRange(namedType.GetMembers()
                .OfType<IMethodSymbol>()
                .Where(m => m.MethodKind == MethodKind.Ordinary)
                .Select(FormatMethod)
                .OrderBy(x => x));
        }
        else if (symbol is IMethodSymbol method)
        {
            parts.Add($"type:{symbol.ToDisplayString()}");
            parts.Add($"access:{symbol.DeclaredAccessibility}");
            parts.Add(ResolveTypeName(method.ReturnType));
            parts.Add(string.Join(",", method.Parameters.Select(p => ResolveTypeName(p.Type))));
        }
        else
        {
            parts.Add($"type:{symbol.ToDisplayString()}");
            parts.Add($"access:{symbol.DeclaredAccessibility}");
        }

        var canonicalized = string.Join("|", parts);
        var hash = XxHash3.Hash(Encoding.UTF8.GetBytes(canonicalized));
        return Convert.ToHexString(hash);
    }

    // These hash generation methods intentionally duplicate traversal logic because their inclusion rules differ:
    // - ComputeFingerprint: Includes ALL members (private, protected, etc.) for complete type representation
    // - ComputeSurfaceHash: Excludes private/protected-and-friend members for surface-level API exposure
    // - ComputeDependencyHash: Focuses only on dependencies, not type structure
    // Hash behavior is part of the semantic-cache trust contract - changes would break cache consistency
    private static string ComputeSurfaceHash(INamedTypeSymbol namedType)
    {
        var parts = new List<string>();

        parts.Add($"type:{namedType.ToDisplayString()}");
        parts.Add($"access:{namedType.DeclaredAccessibility}");
        parts.Add($"kind:{namedType.TypeKind}");
        parts.Add($"ns:{namedType.ContainingNamespace?.ToDisplayString() ?? ""}");

        if (namedType.BaseType != null && namedType.BaseType.SpecialType != SpecialType.System_Object)
            parts.Add($"base:{ResolveTypeName(namedType.BaseType)}");

        parts.AddRange(namedType.AllInterfaces
            .Select(i => $"iface:{ResolveTypeName(i)}")
            .OrderBy(x => x));

        parts.AddRange(namedType.GetAttributes()
            .Select(a => $"attr:{a.AttributeClass?.Name ?? ""}")
            .OrderBy(x => x));

        parts.AddRange(namedType.TypeParameters
            .Select(tp => $"tparam:{tp.Name}|{tp.Variance}")
            .OrderBy(x => x));

        parts.AddRange(namedType.GetMembers()
            .OfType<INamedTypeSymbol>()
            .Where(m => m.DeclaredAccessibility != Accessibility.Private && m.DeclaredAccessibility != Accessibility.ProtectedAndFriend)
            .Select(t => $"nested:{ResolveTypeName(t)}")
            .OrderBy(x => x));

        parts.AddRange(namedType.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Constructor)
            .Select(FormatConstructor)
            .OrderBy(x => x));

        parts.AddRange(namedType.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => f.DeclaredAccessibility != Accessibility.Private && f.DeclaredAccessibility != Accessibility.ProtectedAndFriend)
            .Select(FormatField)
            .OrderBy(x => x));

        parts.AddRange(namedType.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p.DeclaredAccessibility != Accessibility.Private && p.DeclaredAccessibility != Accessibility.ProtectedAndFriend)
            .Select(FormatProperty)
            .OrderBy(x => x));

        parts.AddRange(namedType.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary)
            .Where(m => m.DeclaredAccessibility != Accessibility.Private && m.DeclaredAccessibility != Accessibility.ProtectedAndFriend)
            .Select(FormatMethod)
            .OrderBy(x => x));

        var canonicalized = string.Join("|", parts);
        var hash = XxHash3.Hash(Encoding.UTF8.GetBytes(canonicalized));
        return Convert.ToHexString(hash);
    }

    // These hash generation methods intentionally duplicate traversal logic because their inclusion rules differ:
    // - ComputeFingerprint: Includes ALL members (private, protected, etc.) for complete type representation
    // - ComputeSurfaceHash: Excludes private/protected-and-friend members for surface-level API exposure
    // - ComputeDependencyHash: Focuses only on dependencies, not type structure
    // Hash behavior is part of the semantic-cache trust contract - changes would break cache consistency
    private static string ComputeDependencyHash(INamedTypeSymbol namedType)
    {
        var parts = new List<string>();

        if (namedType.BaseType != null && namedType.BaseType.SpecialType != SpecialType.System_Object)
            parts.Add($"base:{ResolveTypeName(namedType.BaseType)}");

        parts.AddRange(namedType.Interfaces
            .Select(i => $"iface:{ResolveTypeName(i)}")
            .OrderBy(x => x));

        foreach (var tp in namedType.TypeParameters)
        {
            var constraints = tp.ConstraintTypes
                .Select(ResolveTypeName)
                .OrderBy(x => x)
                .ToList();
            if (constraints.Count > 0)
                parts.Add($"tparam_constraint:{tp.Name}:{string.Join(",", constraints)}");
        }

        var externalTypeFqns = new HashSet<string>();
        foreach (var member in namedType.GetMembers())
        {
            switch (member)
            {
                case IFieldSymbol field:
                    externalTypeFqns.Add(ResolveTypeName(field.Type));
                    break;
                case IPropertySymbol prop:
                    externalTypeFqns.Add(ResolveTypeName(prop.Type));
                    break;
                case IMethodSymbol method when method.MethodKind == MethodKind.Constructor:
                    foreach (var p in method.Parameters)
                        externalTypeFqns.Add(ResolveTypeName(p.Type));
                    break;
                case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary:
                    foreach (var p in method.Parameters)
                        externalTypeFqns.Add(ResolveTypeName(p.Type));
                    externalTypeFqns.Add(ResolveTypeName(method.ReturnType));
                    break;
            }
        }

        parts.AddRange(externalTypeFqns
            .Select(f => $"ext:{f}")
            .OrderBy(x => x));

        var canonicalized = string.Join("|", parts);
        var hash = XxHash3.Hash(Encoding.UTF8.GetBytes(canonicalized));
        return Convert.ToHexString(hash);
    }

    private static string FormatTypeParameter(ITypeParameterSymbol tp)
    {
        var constraints = tp.ConstraintTypes
            .Select(ResolveTypeName)
            .OrderBy(x => x);
        var constraintList = string.Join(",", constraints);
        return $"tparam:{tp.Name}|{tp.Variance}|{constraintList}";
    }

    private static string FormatConstructor(IMethodSymbol ctor) =>
        $"ctor:{ctor.DeclaredAccessibility}:{StaticFlag(ctor.IsStatic)}:{ctor.Name}({FormatParameterTypes(ctor)})";

    private static string FormatField(IFieldSymbol field) =>
        $"field:{field.DeclaredAccessibility}:{StaticFlag(field.IsStatic)}:{field.Name}:{ResolveTypeName(field.Type)}";

    private static string FormatProperty(IPropertySymbol prop) =>
        $"prop:{prop.DeclaredAccessibility}:{StaticFlag(prop.IsStatic)}:{prop.Name}:{ResolveTypeName(prop.Type)}:{prop.GetMethod?.DeclaredAccessibility}:{prop.SetMethod?.DeclaredAccessibility}";

    private static string FormatMethod(IMethodSymbol method) =>
        $"method:{method.DeclaredAccessibility}:{StaticFlag(method.IsStatic)}:{method.Name}({FormatParameterTypes(method)})->{ResolveTypeName(method.ReturnType)}";

    private static string FormatParameterTypes(IMethodSymbol method) =>
        string.Join(",", method.Parameters.Select(p => ResolveTypeName(p.Type)));

    private static string StaticFlag(bool isStatic) => isStatic ? "static" : "instance";

    private static string ResolveTypeName(ITypeSymbol type)
    {
        if (type is IErrorTypeSymbol error)
            return $"UNRESOLVED:{error.Name}";

        return type.ToDisplayString();
    }

    private static List<object> GetBlindSpotsForContext(string context)
    {
        var diRegistration = new { vector = "di_registration", determinability = ProvenanceNotDeterminable };
        var frameworkInvocation = new { vector = "framework_invocation", determinability = ProvenanceNotDeterminable };
        var interfacePolymorphism = new { vector = "interface_polymorphism", determinability = ProvenanceNotDeterminable };
        var reflection = new { vector = "reflection", determinability = ProvenanceNotDeterminable };
        var sourceGenerators = new { vector = "source_generators", determinability = ProvenanceNotDeterminable };
        var stringBasedDi = new { vector = "string_based_di", determinability = ProvenanceNotDeterminable };
        var flutterFrontend = new { vector = "flutter_frontend", determinability = ProvenanceNotDeterminable };

        return context switch
        {
            "audit" => new List<object> { diRegistration, frameworkInvocation, interfacePolymorphism, reflection, sourceGenerators },
            "simulate-delete" => new List<object> { reflection, stringBasedDi, flutterFrontend },
            "simulate-rename" => new List<object> { stringBasedDi, reflection },
            "simulate-move" => new List<object> { stringBasedDi, reflection, flutterFrontend, interfacePolymorphism },
            _ => throw new ArgumentException($"Unknown context: {context}")
        };
    }

    private static List<object> GetBlindSpots()
    {
        return GetBlindSpotsForContext("audit");
    }

    private static bool IsGeneratedFile(string filePath)
    {
        var lower = filePath.ToLowerInvariant().Replace('\\', '/');
        return lower.Contains("/migrations/")
            || lower.Contains("/obj/")
            || lower.Contains("/.nuget/")
            || lower.EndsWith(".g.cs")
            || lower.EndsWith(".designer.cs")
            || lower.EndsWith(".generated.cs");
    }

    private static bool IsBclType(ITypeSymbol type)
    {
        var ns = type.ContainingNamespace?.ToDisplayString() ?? "";
        return ns == "System" || ns.StartsWith("System.");
    }

    private static void CollectTypeDeps(HashSet<string> deps, ITypeSymbol type)
    {
        if (!IsBclType(type))
            deps.Add(type.ToDisplayString());

        if (type is INamedTypeSymbol named)
        {
            foreach (var targ in named.TypeArguments)
                CollectTypeDeps(deps, targ);
        }
    }

    private static HashSet<string> CollectExternalTypeFqns(INamedTypeSymbol symbol)
    {
        var deps = new HashSet<string>();

        if (symbol.BaseType != null && symbol.BaseType.SpecialType != SpecialType.System_Object)
            CollectTypeDeps(deps, symbol.BaseType);

        foreach (var iface in symbol.AllInterfaces)
            CollectTypeDeps(deps, iface);

        foreach (var member in symbol.GetMembers())
        {
            if (!SymbolEqualityComparer.Default.Equals(member.ContainingType, symbol))
                continue;

            switch (member)
            {
                case IFieldSymbol field:
                    CollectTypeDeps(deps, field.Type);
                    break;
                case IPropertySymbol prop:
                    CollectTypeDeps(deps, prop.Type);
                    break;
                case IMethodSymbol method when method.MethodKind == MethodKind.Constructor:
                    foreach (var p in method.Parameters)
                        CollectTypeDeps(deps, p.Type);
                    break;
                case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary:
                    foreach (var p in method.Parameters)
                        CollectTypeDeps(deps, p.Type);
                    CollectTypeDeps(deps, method.ReturnType);
                    break;
            }
        }

        return deps;
    }

    private static List<string> ExtractDependencies(INamedTypeSymbol symbol)
    {
        var deps = new HashSet<string>(CollectExternalTypeFqns(symbol));
        deps.Remove(symbol.ToDisplayString());
        return deps.OrderBy(d => d).ToList();
    }

    private static bool InheritsFrom(INamedTypeSymbol? symbol, string fullMetadataName)
    {
        var current = symbol;
        while (current != null)
        {
            var name = current.ToDisplayString();
            var genericIdx = name.IndexOf('<');
            if (genericIdx >= 0 ? name[..genericIdx] == fullMetadataName : name == fullMetadataName)
                return true;
            current = current.BaseType;
        }
        return false;
    }

    private static bool ImplementsInterface(INamedTypeSymbol symbol, string prefix)
    {
        return symbol.AllInterfaces.Any(i =>
        {
            var name = i.ToDisplayString();
            if (name == prefix) return true;
            var genericIdx = name.IndexOf('<');
            return genericIdx >= 0 && name[..genericIdx] == prefix;
        });
    }

    private static (string kind, string kindRule, string kindProvenance) ClassifyType(INamedTypeSymbol symbol, Compilation compilation)
    {
        var name = symbol.Name;
        var nameLower = name.ToLowerInvariant();
        var ns = symbol.ContainingNamespace?.ToDisplayString() ?? "";
        var nsLower = ns.ToLowerInvariant();

        if (InheritsFrom(symbol, "Microsoft.AspNetCore.Mvc.ControllerBase")
            || symbol.GetAttributes()
                .Any(a => a.AttributeClass?.Name == "ApiControllerAttribute" && a.AttributeClass.ContainingNamespace?.ToDisplayString().Contains("Mvc") == true))
        {
            return ("controller", "derives from ControllerBase", "compiler_proved");
        }

        if (ImplementsInterface(symbol, "MediatR.IRequest") || ImplementsInterface(symbol, "MediatR.IRequest<>"))
        {
            return ("command", "implements IRequest", "compiler_proved");
        }

        if (nameLower.EndsWith("command"))
        {
            return ("command", "name ends with Command", "indexer_observed");
        }

        if (ImplementsInterface(symbol, "MediatR.IRequest") || ImplementsInterface(symbol, "MediatR.IRequest<>"))
        {
            return ("query", "implements IRequest", "compiler_proved");
        }

        if (nameLower.EndsWith("query"))
        {
            return ("query", "name ends with Query", "indexer_observed");
        }

        if (symbol.AllInterfaces.Any(i => i.Name.EndsWith("IRepository")) ||
            nsLower.Contains(".repositories"))
        {
            var rule = symbol.AllInterfaces.Any(i => i.Name.EndsWith("IRepository"))
                ? "implements IRepository"
                : "namespace contains Repositories";
            var provenance = symbol.AllInterfaces.Any(i => i.Name.EndsWith("IRepository"))
                ? "compiler_proved"
                : "indexer_observed";
            return ("repository", rule, provenance);
        }

        if (nsLower.Contains(".domain.") || nsLower.Contains(".entities."))
        {
            return ("entity", "namespace contains Domain or Entities", "indexer_observed");
        }

        if (nameLower.EndsWith("service"))
        {
            return ("service", "name ends with Service", "indexer_observed");
        }

        return ("unclassified", "", "indexer_observed");
    }

    private static DiscoveredType BuildDiscoveredType(INamedTypeSymbol symbol, string projectName, string filePath, Compilation compilation)
    {
        var (kind, kindRule, kindProvenance) = ClassifyType(symbol, compilation);
        return new DiscoveredType(
            SymbolId: symbol.ToDisplayString(),
            Kind: symbol.TypeKind.ToString(),
            KindCategory: kind,
            Namespace: symbol.ContainingNamespace?.ToDisplayString() ?? "",
            Project: projectName,
            SourceFile: GetRelativePath(filePath),
            Accessibility: symbol.DeclaredAccessibility.ToString(),
            Inherits: symbol.BaseType != null && symbol.BaseType.SpecialType != SpecialType.System_Object
                ? symbol.BaseType.ToDisplayString()
                : null,
            Implements: symbol.AllInterfaces.Select(i => i.ToDisplayString()).ToList(),
            Dependencies: ExtractDependencies(symbol)
        );
    }

    private static async Task<DiscoverySnapshot> BuildDiscoverySnapshotAsync(Solution solution)
    {
        var projects = new List<ProjectCatalog>();
        var symbols = new List<SymbolRecord>();
        var outgoingEdges = new Dictionary<string, List<string>>();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            foreach (var document in project.Documents)
            {
                var filePath = document.FilePath ?? "";
                if (IsGeneratedFile(filePath)) continue;

                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null) continue;

                var root = await syntaxTree.GetRootAsync();
                var model = compilation.GetSemanticModel(syntaxTree);

                foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol namedSymbol)
                        continue;

                    if (namedSymbol.Locations.Any(loc => loc.IsInMetadata))
                        continue;

                    var line = namedSymbol.Locations
                        .Where(loc => loc.IsInSource)
                        .Select(loc => loc.GetLineSpan().StartLinePosition.Line + 1)
                        .FirstOrDefault();

                    var (kind, kindRule, kindProvenance) = ClassifyType(namedSymbol, compilation);

                    var outgoingTypeNames = CollectExternalTypeFqns(namedSymbol).ToList();

                    var symbolId = namedSymbol.ToDisplayString();
                    if (!outgoingEdges.ContainsKey(symbolId))
                        outgoingEdges[symbolId] = new List<string>();
                    outgoingEdges[symbolId].AddRange(outgoingTypeNames);

                    var symbolRecord = new SymbolRecord(
                        MetadataName: symbolId,
                        Kind: kind,
                        KindRule: kindRule,
                        KindProvenance: kindProvenance,
                        Project: project.Name,
                        File: GetRelativePath(filePath),
                        Line: line,
                        Namespace: namedSymbol.ContainingNamespace?.ToDisplayString() ?? "",
                        IsAbstract: namedSymbol.IsAbstract,
                        IsSealed: namedSymbol.IsSealed,
                        MemberCount: namedSymbol.GetMembers().Count(),
                        OutgoingTypeNames: outgoingTypeNames
                    );

                    symbols.Add(symbolRecord);
                }
            }

            var projectCatalog = new ProjectCatalog(
                Name: project.Name,
                AssemblyName: project.AssemblyName?.ToString() ?? "",
                ProjectPath: project.FilePath ?? "",
                OutputPath: project.OutputFilePath ?? ""
            );
            projects.Add(projectCatalog);
        }

        var incomingEdges = new Dictionary<string, List<string>>();
        foreach (var (sourceSymbol, targetSymbols) in outgoingEdges)
        {
            foreach (var targetSymbol in targetSymbols)
            {
                if (!incomingEdges.ContainsKey(targetSymbol))
                    incomingEdges[targetSymbol] = new List<string>();
                incomingEdges[targetSymbol].Add(sourceSymbol);
            }
        }

        return new DiscoverySnapshot(
            Projects: projects,
            Symbols: symbols,
            IncomingEdges: incomingEdges,
            BuiltAtUtc: DateTime.UtcNow
        );
    }

private static DiscoveredType ProjectSymbolRecordToDiscoveredType(SymbolRecord symbol)
    {
        return new DiscoveredType(
            SymbolId: symbol.MetadataName,
            Kind: symbol.Kind,
            KindCategory: symbol.Kind,
            Namespace: symbol.Namespace,
            Project: symbol.Project,
            SourceFile: symbol.File,
            Accessibility: symbol.Kind == "controller" ? "Public" : "Public",
            Inherits: null,
            Implements: new List<string>(),
            Dependencies: symbol.OutgoingTypeNames
        );
    }

    private static async Task DiscoverAsync(string[] args)
    {
        var kindFilter = args.FirstOrDefault(a => a.StartsWith("--kind="))?.Split('=', 2)[1];
        var projectFilter = args.FirstOrDefault(a => a.StartsWith("--project="))?.Split('=', 2)[1];

        if (!_useJson)
            WriteProgress("Discovering types...");

        var solution = await LoadSolutionAsync();
        var snapshot = await BuildDiscoverySnapshotAsync(solution);

        var filteredSymbols = snapshot.Symbols;
        if (kindFilter != null)
        {
            var filters = kindFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            filteredSymbols = filteredSymbols.Where(s => filters.Contains(s.Kind, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        var allTypes = filteredSymbols.Select(ProjectSymbolRecordToDiscoveredType).ToList();

        var byKindCategory = allTypes.GroupBy(t => t.KindCategory)
            .ToDictionary(g => g.Key, g => g.Count());
        var byProject = allTypes.GroupBy(t => t.Project)
            .ToDictionary(g => g.Key, g => g.GroupBy(t => t.KindCategory).ToDictionary(gg => gg.Key, gg => gg.Count()));

        var summary = new
        {
            totalTypes = allTypes.Count,
            byKindCategory,
            byProject
        };

        if (_useJson)
        {
            WriteJsonResult("discover", new
            {
                summary,
                types = allTypes,
                provenance = GetProvenanceCompilerProved(),
                fieldProvenance = new
                {
                    kind = GetProvenanceCompilerProved(),
                    kindCategory = GetProvenanceIndexerObserved(),
                    ns = GetProvenanceCompilerProved(),
                    project = GetProvenanceCompilerProved(),
                    sourceFile = GetProvenanceCompilerProved(),
                    accessibility = GetProvenanceCompilerProved(),
                    inherits = GetProvenanceCompilerProved(),
                    implements = GetProvenanceCompilerProved(),
                    dependencies = GetProvenanceCompilerProved(),
                    summary = GetProvenanceCompilerProved(),
                    blindSpots = "not_determinable"
                },
                blindSpots = GetBlindSpots()
            });
        }
        else
        {
            Console.WriteLine($"\nDiscovered {allTypes.Count} types across {allTypes.Select(t => t.Project).Distinct().Count()} projects.\n");

            foreach (var cat in byKindCategory.OrderBy(k => k.Key))
            {
                Console.WriteLine($"  {cat.Key}: {cat.Value}");
                var typesInCat = allTypes.Where(t => t.KindCategory == cat.Key).ToList();
                var byProj = typesInCat.GroupBy(t => t.Project);
                foreach (var pg in byProj)
                {
                    Console.WriteLine($"    {pg.Key}:");
                    foreach (var t in pg)
                        Console.WriteLine($"      {t.SymbolId}");
                }
                Console.WriteLine();
            }

            Console.WriteLine("Blind spots:");
            foreach (var bs in GetBlindSpots())
                Console.WriteLine($"  [not_determinable] {bs.GetType().GetProperty("reason")?.GetValue(bs)}");
        }
    }

    private static (string tier, string tierRule) ComputeTier(
        bool externallyExposed, int referencingProjectCount, int incomingCount)
    {
        if (externallyExposed)
            return ("public_surface", "externally exposed (controller, compiler_proved)");
        if (referencingProjectCount >= CrossProjectMinCount)
            return ("cross_project", $"referencingProjectCount >= {CrossProjectMinCount}");
        if (incomingCount > 0)
            return ("project_local", "incomingTypeDependencyCount > 0");
        return ("isolated", "no incoming dependencies and not externally exposed");
    }

    private static string ComputeComplexityTier(
        string symbolId, string project, string accessibility,
        Dictionary<string, HashSet<string>> outgoingEdges,
        Dictionary<string, HashSet<string>> incomingEdges,
        Dictionary<string, string> typeToProject,
        bool externallyExposed)
    {
        var fanOut = outgoingEdges.TryGetValue(symbolId, out var outSet) ? outSet : new HashSet<string>();
        var fanIn = incomingEdges.TryGetValue(symbolId, out var inSet) ? inSet : new HashSet<string>();

        var incomingTypeDependencyCount = fanIn.Count;
        var outgoingTypeDependencyCount = fanOut.Count;
        var referencingProjectCount = incomingEdges
            .Where(kvp => kvp.Value.Contains(symbolId))
            .Select(kvp => typeToProject.GetValueOrDefault(kvp.Key, "(unknown)"))
            .Distinct()
            .Count();

        var (tier, _) = ComputeTier(externallyExposed, referencingProjectCount, incomingTypeDependencyCount);
        return tier;
    }

    private static FanSummary BuildFanSummary(
        string symbolId,
        Dictionary<string, HashSet<string>> edges,
        Dictionary<string, string> typeToProject)
    {
        var sources = edges.TryGetValue(symbolId, out var set)
            ? set.OrderBy(s => s).ToList()
            : new List<string>();

        var byProject = sources
            .GroupBy(s => typeToProject.TryGetValue(s, out var p) ? p : "(unknown)")
            .ToDictionary(g => g.Key, g => g.Count());

        return new FanSummary(sources.Count, sources, byProject);
    }

    private static Dictionary<string, HashSet<string>> BuildOutgoingEdges(
        List<SymbolRecord> symbols)
    {
        var outgoingEdges = new Dictionary<string, HashSet<string>>();
        foreach (var symbol in symbols)
        {
            outgoingEdges[symbol.MetadataName] = new HashSet<string>(symbol.OutgoingTypeNames);
        }
        return outgoingEdges;
    }

    private static Dictionary<string, HashSet<string>> BuildIncomingEdges(
        Dictionary<string, HashSet<string>> outgoingEdges)
    {
        var incomingEdges = new Dictionary<string, HashSet<string>>();
        foreach (var (sourceSymbol, targetSymbols) in outgoingEdges)
        {
            foreach (var targetSymbol in targetSymbols)
            {
                if (!incomingEdges.ContainsKey(targetSymbol))
                    incomingEdges[targetSymbol] = new HashSet<string>();
                incomingEdges[targetSymbol].Add(sourceSymbol);
            }
        }
        return incomingEdges;
    }

    private static Dictionary<string, string> BuildTypeToProjectMap(
        List<SymbolRecord> symbols)
    {
        var typeToProject = new Dictionary<string, string>();
        foreach (var symbol in symbols)
        {
            typeToProject[symbol.MetadataName] = symbol.Project;
        }
        return typeToProject;
    }

    private static Dictionary<string, string> BuildTypeToAccessibilityMap(
        List<SymbolRecord> symbols)
    {
        var typeToAccessibility = new Dictionary<string, string>();
        foreach (var symbol in symbols)
        {
            typeToAccessibility[symbol.MetadataName] = "Public";
        }
        return typeToAccessibility;
    }

    private static (List<string> incomingList, int incomingCount) GetIncomingEdges(
        DiscoverySnapshot snapshot, string fqn)
    {
        var incomingList = snapshot.IncomingEdges.TryGetValue(fqn, out var list)
            ? list.Distinct().ToList()
            : new List<string>();
        var incomingCount = incomingList.Count;
        return (incomingList, incomingCount);
    }

    private static int GetReferencingProjectCount(
        List<string> incomingList,
        Dictionary<string, string> fqnToProject)
    {
        return incomingList != null
            ? incomingList.Distinct()
                .Select(src => fqnToProject.TryGetValue(src, out var p) ? p : null)
                .Where(p => p != null)
                .Cast<string>()
                .Distinct()
                .Count()
            : 0;
    }

    private static string GetProvenanceIndexerObserved() => "indexer_observed";
    private static string GetProvenanceCompilerProved() => "compiler_proved";

    private static async Task StructureAsync(string[] args)
    {
        var symbolFilter = args.FirstOrDefault(a => a.StartsWith("--symbol="))?.Split('=', 2)[1];
        var depth = 1;
        var depthArg = args.FirstOrDefault(a => a.StartsWith("--depth="));
        if (depthArg != null)
            int.TryParse(depthArg.Split('=', 2)[1], out depth);

        if (string.IsNullOrEmpty(symbolFilter))
        {
            if (_useJson)
                WriteJsonResult("structure", new { error = "Missing required argument --symbol=Namespace.ClassName" });
            else
                Console.WriteLine("Usage: --mode=structure --symbol=Namespace.ClassName [--depth=2] [--json]");
            return;
        }

        if (!_useJson)
            WriteProgress("Analyzing structure...");

        var solution = await LoadSolutionAsync();
        var snapshot = await BuildDiscoverySnapshotAsync(solution);

        var symbolRecord = snapshot.Symbols.FirstOrDefault(s => s.MetadataName == symbolFilter);
        if (symbolRecord == null)
        {
            if (_useJson)
                WriteJsonResult("structure", new { symbol = symbolFilter, resolved = false, error = "Symbol not found" });
            else
                Console.WriteLine($"Symbol not found: {symbolFilter}");
            return;
        }

        var outgoingEdges = BuildOutgoingEdges(snapshot.Symbols);
        var incomingEdges = BuildIncomingEdges(outgoingEdges);
        var typeToProject = BuildTypeToProjectMap(snapshot.Symbols);
        var typeToAccessibility = BuildTypeToAccessibilityMap(snapshot.Symbols);

        var fanIn = BuildFanSummary(symbolFilter, incomingEdges, typeToProject);
        var fanOut = BuildFanSummary(symbolFilter, outgoingEdges, typeToProject);

        var externallyExposed = symbolRecord.Kind == "controller";

        var tier = ComputeComplexityTier(
            symbolFilter, typeToProject[symbolFilter], typeToAccessibility[symbolFilter],
            outgoingEdges, incomingEdges, typeToProject, externallyExposed);

        object? depth2 = null;
        if (depth >= 2)
        {
            var expandedFanOut = fanOut.Sources
                .ToDictionary(s => s, s => outgoingEdges.TryGetValue(s, out var d) ? d.Count : 0);
            var expandedFanIn = fanIn.Sources
                .ToDictionary(s => s, s => incomingEdges.TryGetValue(s, out var d) ? d.Count : 0);
            depth2 = new { fanOutCounts = expandedFanOut, fanInCounts = expandedFanIn };
        }

        if (_useJson)
        {
            WriteJsonResult("structure", new
            {
                symbol = symbolFilter,
                resolved = true,
                kind = symbolRecord.Kind,
                kindCategory = symbolRecord.Kind,
                ns = symbolRecord.Namespace,
                project = symbolRecord.Project,
                sourceFile = symbolRecord.File,
                accessibility = typeToAccessibility[symbolFilter],
                inherits = null as string,
                implements = new List<string>(),
                complexityTier = tier,
                fanIn,
                fanOut,
                depth2,
                provenance = GetProvenanceCompilerProved(),
                fieldProvenance = new
                {
                    kind = GetProvenanceCompilerProved(),
                    kindCategory = GetProvenanceIndexerObserved(),
                    ns = GetProvenanceCompilerProved(),
                    project = GetProvenanceCompilerProved(),
                    sourceFile = GetProvenanceCompilerProved(),
                    accessibility = GetProvenanceCompilerProved(),
                    inherits = GetProvenanceCompilerProved(),
                    implements = GetProvenanceCompilerProved(),
                    complexityTier = GetProvenanceIndexerObserved(),
                    fanIn = GetProvenanceCompilerProved(),
                    fanOut = GetProvenanceCompilerProved(),
                    depth2 = GetProvenanceCompilerProved(),
                    blindSpots = "not_determinable"
                },
                blindSpots = GetBlindSpots()
            });
        }
        else
        {
            Console.WriteLine($"\nSymbol: {symbolFilter}");
            Console.WriteLine($"  Kind:           {symbolRecord.Kind} ({symbolRecord.Kind})");
            Console.WriteLine($"  Namespace:      {symbolRecord.Namespace}");
            Console.WriteLine($"  Project:        {symbolRecord.Project}");
            Console.WriteLine($"  Source file:    {symbolRecord.File}");
            Console.WriteLine($"  Accessibility:  {typeToAccessibility[symbolFilter]}");
            Console.WriteLine($"  Complexity tier: {tier}");
            Console.WriteLine($"\n  Fan-in:  {fanIn.Count}");
            foreach (var s in fanIn.Sources)
                Console.WriteLine($"    ← {s}");
            Console.WriteLine($"\n  Fan-out: {fanOut.Count}");
            foreach (var s in fanOut.Sources)
                Console.WriteLine($"    → {s}");

            if (depth2 != null)
            {
                Console.WriteLine("\n  Depth-2 fan-out counts:");
                var d2Out = ((dynamic)depth2).fanOutCounts;
                foreach (var kv in (Dictionary<string, int>)d2Out)
                    Console.WriteLine($"    {kv.Key}: {kv.Value}");
                Console.WriteLine("  Depth-2 fan-in counts:");
                var d2In = ((dynamic)depth2).fanInCounts;
                foreach (var kv in (Dictionary<string, int>)d2In)
                    Console.WriteLine($"    {kv.Key}: {kv.Value}");
            }
        }
    }

    private static async Task WhoReferencesAsync(string symbolName)
    {
        var solution = await LoadSolutionAsync();
        var (symbol, _) = await FindTypeSymbolAsync(solution, symbolName);

        if (symbol == null)
        {
            if (_useJson)
                WriteJsonResult("who-references", new
                {
                    symbol = symbolName,
                    resolved = false,
                    error = "Symbol not found",
                    blindSpots = GetBlindSpots(),
                    provenance = "compiler_proved"
                });
            else
                Console.WriteLine($"Symbol not found: {symbolName}");
            Environment.Exit(1);
            return;
        }

        var referencedSymbols = await SymbolFinder.FindReferencesAsync(symbol, solution);
        var allRefs = referencedSymbols.SelectMany(r => r.Locations).ToList();

        var referenceDetails = new List<ReferenceDetail>();
        foreach (var refLoc in allRefs)
        {
            string? containingSymbol = null;
            var refKind = "type_reference";
            try
            {
                var doc = refLoc.Document;
                var syntaxTree = await doc.GetSyntaxTreeAsync();
                if (syntaxTree != null)
                {
                    var root = await syntaxTree.GetRootAsync();
                    var token = root.FindToken(refLoc.Location.SourceSpan.Start);
                    var refNode = token.Parent;
                    refKind = refNode switch
                    {
                        ObjectCreationExpressionSyntax => "object_creation",
                        InvocationExpressionSyntax => "invocation",
                        SimpleBaseTypeSyntax => "base_list",
                        _ => "type_reference"
                    };
                    var node = refNode;
                    while (node != null && !(node is BaseTypeDeclarationSyntax
                        || node is MethodDeclarationSyntax
                        || node is PropertyDeclarationSyntax
                        || node is ConstructorDeclarationSyntax
                        || node is BaseFieldDeclarationSyntax))
                    {
                        node = node.Parent;
                    }
                    if (node != null)
                    {
                        var model = await doc.GetSemanticModelAsync();
                        if (model != null)
                        {
                            var declaredSymbol = model.GetDeclaredSymbol(node);
                            if (declaredSymbol != null)
                                containingSymbol = declaredSymbol.ToDisplayString();
                        }
                    }
                }
            }
            catch {  }

            var lineSpan = refLoc.Location.GetLineSpan();
            referenceDetails.Add(new ReferenceDetail(
                File: refLoc.Location.SourceTree?.FilePath != null
                    ? GetRelativePath(refLoc.Location.SourceTree.FilePath)
                    : refLoc.Location.SourceTree?.FilePath,
                Line: lineSpan.StartLinePosition.Line + 1,
                Project: refLoc.Document.Project.Name,
                ContainingSymbol: containingSymbol,
                LocationProvenance: "compiler_proved",
                Kind: refKind,
                KindProvenance: "indexer_observed"
            ));
        }

        var declarationSites = symbol.Locations
            .Where(l => l.IsInSource)
            .Select(l =>
            {
                var lineSpan = l.GetLineSpan();
                return new DeclarationSite(
                    File: l.SourceTree?.FilePath != null
                        ? GetRelativePath(l.SourceTree.FilePath)
                        : l.SourceTree?.FilePath,
                    Line: lineSpan.StartLinePosition.Line + 1
                );
            })
            .ToList();

        var uniqueFilesSet = new HashSet<string>();
        var uniqueProjectsSet = new HashSet<string>();
        var uniqueContainingSymbolsSet = new HashSet<string>();
        var projectCounts = new Dictionary<string, int>();
        var fileCounts = new Dictionary<string, int>();
        var containingSymbolCounts = new Dictionary<string, int>();
        var generatedCount = 0;

        foreach (var rd in referenceDetails)
        {
            string? file = rd.File;
            string? project = rd.Project;
            string? containingSymbol = rd.ContainingSymbol;

            if (file != null)
            {
                uniqueFilesSet.Add(file);
                fileCounts.TryGetValue(file, out var fc);
                fileCounts[file] = fc + 1;

                var lower = file.ToLowerInvariant();
                if (lower.Contains("/migrations/") ||
                    lower.Contains(".g.cs") ||
                    lower.Contains(".designer.cs") ||
                    lower.Contains(".generated.cs") ||
                    lower.Contains("/obj/"))
                {
                    generatedCount++;
                }
            }

            if (project != null)
            {
                uniqueProjectsSet.Add(project);
                projectCounts.TryGetValue(project, out var pc);
                projectCounts[project] = pc + 1;
            }

            if (!string.IsNullOrEmpty(containingSymbol))
            {
                uniqueContainingSymbolsSet.Add(containingSymbol);
                containingSymbolCounts.TryGetValue(containingSymbol, out var sc);
                containingSymbolCounts[containingSymbol] = sc + 1;
            }
        }

        var declSiteKeys = new HashSet<string>();
        foreach (var decl in declarationSites)
        {
            string? file = decl.File;
            int line = decl.Line;
            if (file != null)
                declSiteKeys.Add($"{file}:{line}");
        }

        var selfReferenceCount = 0;
        foreach (var rd in referenceDetails)
        {
            string? file = rd.File;
            int line = rd.Line;
            if (file != null && declSiteKeys.Contains($"{file}:{line}"))
                selfReferenceCount++;
        }

        var fieldProvenance = new
        {
            referenceCount = ProvenanceCompilerProved,
            references = ProvenanceCompilerProved,
            declarationSites = ProvenanceCompilerProved,
            uniqueFiles = ProvenanceCompilerProved,
            uniqueProjects = ProvenanceCompilerProved,
            uniqueContainingSymbols = ProvenanceCompilerProved,
            referenceBuckets = ProvenanceCompilerProved,
            generatedReferenceCount = ProvenanceIndexerObserved,
            selfReferenceCount = selfReferenceCount > 0 ? ProvenanceCompilerProved : ProvenanceIndexerObserved,
            externalReferenceCount = ProvenanceIndexerObserved,
            blindSpots = ProvenanceNotDeterminable
        };

        var blindSpots = GetBlindSpots();
        if (selfReferenceCount == 0)
        {
            blindSpots.Add(new
            {
                reason = "self_reference_classification",
                detail = "File+line matching between declarations and references yields 0; deeper semantic check needed for true self-references",
                provenance = "not_determinable"
            });
        }

        if (_useJson)
        {
            WriteJsonResult("who-references", new
            {
                symbol = symbolName,
                resolved = true,
                declarationSites,
                referenceCount = new
                {
                    external = allRefs.Count - selfReferenceCount - generatedCount,
                    self = selfReferenceCount,
                    generatedCode = generatedCount
                },
                references = referenceDetails,
                uniqueFiles = uniqueFilesSet.Count,
                uniqueProjects = uniqueProjectsSet.Count,
                uniqueContainingSymbols = uniqueContainingSymbolsSet.Count,
                referenceBuckets = new
                {
                    byProject = projectCounts,
                    byFile = fileCounts,
                    byContainingSymbol = containingSymbolCounts
                },
                generatedReferenceCount = generatedCount,
                selfReferenceCount,
                externalReferenceCount = allRefs.Count - selfReferenceCount - generatedCount,
                blindSpots,
                provenance = "compiler_proved",
                fieldProvenance
            });
        }
        else
        {
            Console.WriteLine($"Symbol: {symbolName}");
            Console.WriteLine($"Resolved: true");
            foreach (var decl in declarationSites)
                Console.WriteLine($"  Declaration: {decl.File}:{decl.Line}");
            Console.WriteLine($"References found: {allRefs.Count}");
            foreach (var rd in referenceDetails)
            {
                var containing = rd.ContainingSymbol != null
                    ? $" -- containing symbol: {rd.ContainingSymbol}"
                    : "";
                Console.WriteLine($"  {rd.File}:{rd.Line} (project: {rd.Project}){containing}");
            }
            Console.WriteLine("Blind spots: reflection_and_strings, string_based_DI_registration, configuration_strings, external_consumers_flutter_frontend");
        }
    }

    private static async Task ComputeFingerprintOnlyAsync(string symbolName)
    {
        var solution = await LoadSolutionAsync();
        var (symbol, compilation) = await FindTypeSymbolAsync(solution, symbolName);
        if (symbol == null)
        {
            if (_useJson)
                WriteJsonResult("fingerprint", new { symbol = symbolName, error = "Symbol not found" });
            else
                Console.WriteLine($"Symbol not found: {symbolName}");
            return;
        }

        if (compilation == null)
        {
            if (_useJson)
                WriteJsonResult("fingerprint", new { symbol = symbolName, error = "Containing compilation not found" });
            else
                Console.WriteLine($"Containing compilation not found for: {symbolName}");
            return;
        }

        if (symbol is INamedTypeSymbol namedType)
        {
            var surfaceHash = ComputeSurfaceHash(namedType);
            var dependencyHash = ComputeDependencyHash(namedType);
            var hashedDimensions = new[] { "baseType", "interfaces", "attributes", "ctors:all", "members:nonPrivate", "fieldTypes:all", "propertyTypes:all", "methodSignatures:all" };
            if (_useJson)
                WriteJsonResult("fingerprint", new { symbol = symbolName, fingerprint = new { surfaceHash, dependencyHash, hashedDimensions }, provenance = "compiler_proved" });
            else
                Console.WriteLine($"SurfaceHash:     {surfaceHash}\nDependencyHash:  {dependencyHash}");
        }
        else
        {
            var fp = ComputeFingerprint(symbol, compilation);
            if (_useJson)
                WriteJsonResult("fingerprint", new { symbol = symbolName, fingerprint = fp, provenance = "compiler_proved" });
            else
                Console.WriteLine($"Fingerprint: {fp}");
        }
    }

    private static async Task RecomputeAllFingerprintsAsync()
    {
        var semanticFiles = Directory.GetFiles(SemanticDir, "*.semantic.json");
        if (semanticFiles.Length == 0)
        {
            if (_useJson)
                WriteJsonResult("recompute-all", new { totalFiles = 0, same = 0, updated = 0, unmatched = new List<string>() });
            else
                Console.WriteLine("No semantic files found.");
            return;
        }

        WriteProgress($"Recomputing fingerprints for {semanticFiles.Length} curated symbol(s)...");

        var curatedSymbols = new Dictionary<string, string>();
        foreach (var sf in semanticFiles)
        {
            var text = await File.ReadAllTextAsync(sf);
            var node = JsonNode.Parse(text)?.AsObject();
            var symbolId = node?["symbolId"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(symbolId))
                curatedSymbols[symbolId] = sf;
        }

        WriteProgress("  Loading solution...");
        var solution = await LoadSolutionAsync();

        var updated = 0;
        var same = 0;
        var matched = new HashSet<string>();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            foreach (var document in project.Documents)
            {
                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null) continue;

                var root = await syntaxTree.GetRootAsync();
                var model = compilation.GetSemanticModel(syntaxTree);

                foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol namedSymbol) continue;

                    var symbolId = namedSymbol.ToDisplayString();
                    if (!curatedSymbols.TryGetValue(symbolId, out var sfPath)) continue;
                    if (!matched.Add(symbolId)) continue;

                    var newSurfaceHash = ComputeSurfaceHash(namedSymbol);
                    var newDependencyHash = ComputeDependencyHash(namedSymbol);

                    var fileText = await File.ReadAllTextAsync(sfPath);
                    var node = JsonNode.Parse(fileText)?.AsObject();
                    if (node == null) continue;

                    var fpNode = node["fingerprint"];
                    var isV1 = fpNode is JsonValue;
                    string? oldSurfaceHash = null, oldDependencyHash = null;

                    if (!isV1 && fpNode is JsonObject fpObj)
                    {
                        oldSurfaceHash = fpObj["surfaceHash"]?.GetValue<string>();
                        oldDependencyHash = fpObj["dependencyHash"]?.GetValue<string>();
                    }

                    if (!isV1 && newSurfaceHash == oldSurfaceHash && newDependencyHash == oldDependencyHash)
                    {
                        WriteProgress($"  [SAME]    {symbolId}");
                        same++;
                        continue;
                    }

                    node["fingerprint"] = new JsonObject
                    {
                        ["surfaceHash"] = newSurfaceHash,
                        ["dependencyHash"] = newDependencyHash
                    };
                    var json = node.ToJsonString(JsonOptions);
                    await AtomicWriteAsync(sfPath, json);

                    WriteProgress($"  [UPDATED] {symbolId}");
                    updated++;
                }
            }
        }

        var unmatched = curatedSymbols.Keys.Except(matched).ToList();
        if (unmatched.Count > 0)
        {
            WriteProgress("  [WARN] Could not locate in compilation (manual update needed):");
            foreach (var u in unmatched)
                WriteProgress($"    {u}");
        }

        if (_useJson)
            WriteJsonResult("recompute-all", new
            {
                totalFiles = semanticFiles.Length,
                same,
                updated,
                unmatched,
                provenance = "compiler_proved"
            });
        else
            Console.WriteLine($"Recompute complete. Updated: {updated}/{semanticFiles.Length}.");
    }

    private static async Task<object?> MarkDirtyAsync(string[] args)
    {
        var filesPath = args.FirstOrDefault(a => a.StartsWith("--files="))?.Split('=', 2)[1];
        var deletedPath = args.FirstOrDefault(a => a.StartsWith("--deleted="))?.Split('=', 2)[1];

        var dirtyFiles = filesPath != null && File.Exists(filesPath)
            ? (await File.ReadAllLinesAsync(filesPath))
                .Select(l => l.Trim().Replace('\\', '/'))
                .Where(l => l.Length > 0)
                .ToList()
            : new List<string>();

        var deletedFiles = deletedPath != null && File.Exists(deletedPath)
            ? (await File.ReadAllLinesAsync(deletedPath))
                .Select(l => l.Trim().Replace('\\', '/'))
                .Where(l => l.Length > 0)
                .ToList()
            : new List<string>();

        var existing = new DirtyManifest(VersionConstants.OutputSchemaVersion, [], [], "");

        if (File.Exists(DirtyFilePath))
        {
            var text = await File.ReadAllTextAsync(DirtyFilePath);
            existing = JsonSerializer.Deserialize<DirtyManifest>(text, JsonOptions) ?? existing;
        }

        var merged = new DirtyManifest(
            SchemaVersion: VersionConstants.OutputSchemaVersion,
            DirtyFiles: (existing.DirtyFiles ?? []).Union(dirtyFiles).Distinct().ToList(),
            DeletedFiles: (existing.DeletedFiles ?? []).Union(deletedFiles).Distinct().ToList(),
            MarkedAt: DateTime.UtcNow.ToString("O")
        );

        var json = JsonSerializer.Serialize(merged, JsonOptions);
        await AtomicWriteAsync(DirtyFilePath, json);

        if (_useJson)
            return new { dirtyFiles = merged.DirtyFiles, deletedFiles = merged.DeletedFiles, markedAt = merged.MarkedAt, provenance = GetProvenanceIndexerObserved() };
        return null;
    }

    private static async Task SweepAsync()
    {
        if (!File.Exists(DirtyFilePath))
        {
            if (_useJson)
                WriteJsonResult("sweep", new { dirtyProcessed = 0, deletedProcessed = 0, flaggedStale = 0, flaggedDependents = 0, manifestCleared = false, message = "No dirty-files.json found" });
            else
                Console.WriteLine("No dirty-files.json found — nothing to sweep.");
            return;
        }

        var manifestText = await File.ReadAllTextAsync(DirtyFilePath);
        var manifest = JsonSerializer.Deserialize<DirtyManifest>(manifestText, JsonOptions);

        if (manifest == null || ((manifest.DirtyFiles == null || manifest.DirtyFiles.Count == 0) && (manifest.DeletedFiles == null || manifest.DeletedFiles.Count == 0)))
        {
            if (_useJson)
                WriteJsonResult("sweep", new { dirtyProcessed = 0, deletedProcessed = 0, flaggedStale = 0, flaggedDependents = 0, manifestCleared = true, message = "Manifest is empty" });
            else
                Console.WriteLine("Manifest is empty — nothing to sweep.");
            return;
        }

        WriteProgress($"Sweeping {manifest.DirtyFiles?.Count ?? 0} dirty, {manifest.DeletedFiles?.Count ?? 0} deleted...");

        var sourceFileToSymbolIds = new Dictionary<string, List<string>>();
        var curatedFingerprints = new Dictionary<string, (string? Surface, string? Dependency, bool IsV1)>();
        var dependentSymbolIds = new Dictionary<string, List<string>>();

        var semanticFiles = Directory.GetFiles(SemanticDir, "*.semantic.json");
        foreach (var sf in semanticFiles)
        {
            var text = await File.ReadAllTextAsync(sf);
            var node = JsonNode.Parse(text)?.AsObject();
            if (node == null) continue;

            var symbolId = node["symbolId"]?.GetValue<string>();
            var fpNode = node["fingerprint"];
            var sourceFile = node["facts"]?["sourceFile"]?.GetValue<string>();

            if (symbolId == null || sourceFile == null) continue;

            string? storedSurface = null, storedDependency = null;
            bool isV1Fingerprint = fpNode is JsonValue;

            if (!isV1Fingerprint && fpNode is JsonObject fpObj)
            {
                storedSurface = fpObj["surface"]?.GetValue<string>();
                storedDependency = fpObj["dependency"]?.GetValue<string>();
            }

            if (!isV1Fingerprint && (storedSurface == null || storedDependency == null)) continue;

            if (!sourceFileToSymbolIds.ContainsKey(sourceFile))
                sourceFileToSymbolIds[sourceFile] = new List<string>();
            sourceFileToSymbolIds[sourceFile].Add(symbolId);
            curatedFingerprints[symbolId] = (storedSurface, storedDependency, isV1Fingerprint);

            var collaborators = node["interpretation"]?["collaborators"]?.AsArray();
            if (collaborators != null)
            {
                foreach (var c in collaborators)
                {
                    var depSymbol = c?["symbol"]?.GetValue<string>();
                    if (depSymbol == null) continue;
                    if (!dependentSymbolIds.ContainsKey(depSymbol))
                        dependentSymbolIds[depSymbol] = new List<string>();
                    if (!dependentSymbolIds[depSymbol].Contains(symbolId))
                        dependentSymbolIds[depSymbol].Add(symbolId);
                }
            }
        }

        var curatedSymbolIds = new HashSet<string>(curatedFingerprints.Keys);
        WriteProgress($"  Curated entries: {curatedSymbolIds.Count} symbols across {semanticFiles.Length} semantic files.");

        var processed = new List<string>();
        var flaggedStaleCount = 0;
        var flaggedDepCount = 0;

        foreach (var deletedFile in manifest.DeletedFiles ?? [])
        {
            if (sourceFileToSymbolIds.TryGetValue(deletedFile, out var symbolIds))
            {
                foreach (var sid in symbolIds)
                {
                    await FlagSemanticStaleAsync(sid, "source_deleted");
                    flaggedStaleCount++;
                    flaggedDepCount += await FlagDependentsStaleAsync(sid, dependentSymbolIds);
                }
            }
            processed.Add(deletedFile);
        }

        var relevantDirtyFiles = (manifest.DirtyFiles ?? [])
            .Where(f => sourceFileToSymbolIds.ContainsKey(f))
            .ToList();

        WriteProgress($"  Relevant dirty files (matching curated symbols): {relevantDirtyFiles.Count}");

        if (relevantDirtyFiles.Count > 0)
        {
            WriteProgress("  Loading solution...");
            var solution = await LoadSolutionAsync();

            foreach (var dirtyFile in relevantDirtyFiles)
            {
                var documents = solution.Projects
                    .SelectMany(p => p.Documents)
                    .Where(d => GetRelativePath(d.FilePath ?? "") == dirtyFile)
                    .ToList();

                foreach (var doc in documents)
                {
                    var compilation = await doc.Project.GetCompilationAsync();
                    if (compilation == null) continue;

                    var syntaxTree = await doc.GetSyntaxTreeAsync();
                    if (syntaxTree == null) continue;

                    var root = await syntaxTree.GetRootAsync();
                    var model = compilation.GetSemanticModel(syntaxTree);

                    var typeDeclarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>();
                    foreach (var typeDecl in typeDeclarations)
                    {
                        var declaredSymbol = model.GetDeclaredSymbol(typeDecl);
                        if (declaredSymbol is not INamedTypeSymbol namedSymbol) continue;

                        var symbolId = namedSymbol.ToDisplayString();
                        if (!curatedSymbolIds.Contains(symbolId)) continue;

                        if (!curatedFingerprints.TryGetValue(symbolId, out var fpInfo)) continue;

                        if (fpInfo.IsV1)
                        {
                            WriteProgress($"  V1 fingerprint detected for: {symbolId} — marking as fingerprint_v1_migration");
                            await FlagSemanticStaleAsync(symbolId, "fingerprint_v1_migration");
                            flaggedStaleCount++;
                            flaggedDepCount += await FlagDependentsStaleAsync(symbolId, dependentSymbolIds);
                            continue;
                        }

                        var newSurfaceHash = ComputeSurfaceHash(namedSymbol);
                        var newDependencyHash = ComputeDependencyHash(namedSymbol);
                        var surfaceChanged = newSurfaceHash != fpInfo.Surface;
                        var dependencyChanged = newDependencyHash != fpInfo.Dependency;
                        if (surfaceChanged || dependencyChanged)
                        {
                            if (surfaceChanged)
                            {
                                WriteProgress(dependencyChanged
                                    ? $"  Surface and dependencies changed for: {symbolId}"
                                    : $"  Surface changed for: {symbolId}");
                                await FlagSemanticStaleAsync(symbolId, "surface_changed");
                            }
                            else
                            {
                                WriteProgress($"  Dependencies changed for: {symbolId}");
                                await FlagSemanticStaleAsync(symbolId, "dependencies_changed");
                            }
                            flaggedStaleCount++;
                            flaggedDepCount += await FlagDependentsStaleAsync(symbolId, dependentSymbolIds);
                        }
                    }
                }

                processed.Add(dirtyFile);
            }
        }

        foreach (var dirtyFile in (manifest.DirtyFiles ?? []).Except(processed))
        {
            processed.Add(dirtyFile);
        }

        var remaining = new DirtyManifest(
            SchemaVersion: VersionConstants.OutputSchemaVersion,
            DirtyFiles: (manifest.DirtyFiles ?? []).Except(processed).ToList(),
            DeletedFiles: (manifest.DeletedFiles ?? []).Except(processed).ToList(),
            MarkedAt: DateTime.UtcNow.ToString("O")
        );

        var json = JsonSerializer.Serialize(remaining, JsonOptions);
        await AtomicWriteAsync(DirtyFilePath, json);

        var dirtyCount = manifest.DirtyFiles?.Count ?? 0;
        var deletedCount = manifest.DeletedFiles?.Count ?? 0;
        var manifestCleared = remaining.DirtyFiles.Count == 0 && remaining.DeletedFiles.Count == 0;

        if (_useJson)
            WriteJsonResult("sweep", new
            {
                dirtyProcessed = dirtyCount,
                deletedProcessed = deletedCount,
                flaggedStale = flaggedStaleCount,
                flaggedDependents = flaggedDepCount,
                manifestCleared,
                provenance = ProvenanceCompilerProved,
                fieldProvenance = new
                {
                    flaggedStale = ProvenanceCompilerProved,
                    flaggedDependents = ProvenanceCacheSuggests,
                    manifestCleared = ProvenanceIndexerObserved,
                    dirtyProcessed = ProvenanceIndexerObserved,
                    deletedProcessed = ProvenanceIndexerObserved
                }
            });
        else
            Console.WriteLine("Sweep complete.");
    }

    private static async Task<bool> FlagSemanticStaleAsync(string symbolId, string reason)
    {
        var semanticPath = Path.Combine(SemanticDir, $"{SanitizeId(symbolId)}.semantic.json");
        if (!File.Exists(semanticPath))
            return false;

        var text = await File.ReadAllTextAsync(semanticPath);
        var node = JsonNode.Parse(text)?.AsObject();
        if (node == null)
            return false;

        if (node["status"]?.GetValue<string>() == "stale")
            return false;

        node["status"] = "stale";
        node["staleReason"] = reason;

        var json = node.ToJsonString(JsonOptions);
        await AtomicWriteAsync(semanticPath, json);

        WriteProgress($"  Semantic entry flagged stale ({reason}): {symbolId}");
        return true;
    }

    private static async Task<int> FlagDependentsStaleAsync(string symbolId, Dictionary<string, List<string>> dependentSymbolIds)
    {
        if (!dependentSymbolIds.TryGetValue(symbolId, out var dependents)) return 0;
        var count = 0;
        foreach (var dep in dependents)
            count += await FlagSemanticStaleAsync(dep, $"dependency_stale:{symbolId}") ? 1 : 0;
        return count;
    }

    private static async Task LintModeAsync()
    {
        var semanticFiles = Directory.GetFiles(SemanticDir, "*.semantic.json");
        var foundIssues = false;
        var pascalRegex = new System.Text.RegularExpressions.Regex(@"^[A-Z][a-z]+(?:[A-Z][a-z]+)*$");
        var violations = new List<object>();

        foreach (var sf in semanticFiles)
        {
            string text;
            try { text = await File.ReadAllTextAsync(sf); }
            catch (Exception ex) { WriteProgress($"ERROR reading {sf}: {ex.Message}"); continue; }

            JsonNode? root;
            try { root = JsonNode.Parse(text); }
            catch (Exception ex) { WriteProgress($"ERROR parsing {sf}: {ex.Message}"); continue; }

            var collaborators = root?["interpretation"]?["collaborators"]?.AsArray();
            if (collaborators == null) continue;

            for (int i = 0; i < collaborators.Count; i++)
            {
                var rel = collaborators[i]?["relationship"]?.GetValue<string>();
                if (rel != null && !pascalRegex.IsMatch(rel))
                {
                    var fileName = Path.GetFileName(sf);
                    if (_useJson)
                        violations.Add(new { file = fileName, index = i, relationship = rel });
                    else
                        Console.WriteLine($"[WARN] {fileName}: collaborators[{i}].relationship = \"{rel}\" — expected PascalCase");
                    foundIssues = true;
                }
            }
        }

        if (_useJson)
        {
            WriteJsonResult("lint", new { ok = !foundIssues, violations, provenance = GetProvenanceIndexerObserved() });
        }
        else
        {
            if (!foundIssues)
                Console.WriteLine("Lint OK — all relationship values use PascalCase.");
        }
    }

    private static object BuildImpactTierFieldProvenance()
    {
        return new
        {
            affected = ProvenanceCacheSuggests,
            incomingTypeDependencyCount = ProvenanceCompilerProved,
            referencingProjectCount = ProvenanceCompilerProved,
            externallyExposed = ProvenanceCompilerProved,
            tier = ProvenanceIndexerObserved,
            tierRule = ProvenanceIndexerObserved
        };
    }

    private static object BuildImpactEntry(
        string semanticFile, string reason, string via, string provenance, string symbolId,
        Dictionary<string, SymbolRecord>? fqnToSymbol,
        Dictionary<string, string>? fqnToProject,
        Dictionary<string, List<string>>? incomingEdges)
    {
        if (fqnToSymbol == null || !fqnToSymbol.TryGetValue(symbolId, out var symbol))
        {
            return new
            {
                semanticFile,
                reason,
                via,
                provenance,
                incomingTypeDependencyCount = (int?)null,
                referencingProjectCount = (int?)null,
                externallyExposed = (bool?)null,
                tier = (string?)null,
                tierRule = (string?)null
            };
        }

        List<string>? incomingList = null;
        var incomingCount = 0;
        if (incomingEdges!.TryGetValue(symbolId, out var rawList))
        {
            incomingList = rawList;
            incomingCount = rawList.Distinct().Count();
        }

        var referencingProjectCount = incomingList != null
            ? incomingList.Distinct()
                .Select(src => fqnToProject!.TryGetValue(src, out var p) ? p : null)
                .Where(p => p != null)
                .Cast<string>()
                .Distinct()
                .Count()
            : 0;

        var externallyExposed = symbol.Kind == "controller" && symbol.KindProvenance == "compiler_proved";

        var (tier, tierRule) = ComputeTier(externallyExposed, referencingProjectCount, incomingCount);

        return new
        {
            semanticFile,
            reason,
            via,
            provenance,
            incomingTypeDependencyCount = (int?)incomingCount,
            referencingProjectCount = (int?)referencingProjectCount,
            externallyExposed = (bool?)externallyExposed,
            tier = (string?)tier,
            tierRule = (string?)tierRule
        };
    }

    private static async Task ImpactModeAsync()
    {
        if (!File.Exists(DirtyFilePath))
        {
            if (_useJson)
                WriteJsonResult("impact", new { affected = new List<object>(), provenance = ProvenanceIndexerObserved, fieldProvenance = new { affected = ProvenanceIndexerObserved }, tiersAvailable = false });
            else
                Console.WriteLine("[]");
            return;
        }

        var manifestText = await File.ReadAllTextAsync(DirtyFilePath);
        var manifest = JsonSerializer.Deserialize<DirtyManifest>(manifestText, JsonOptions);

        if (manifest == null || ((manifest.DirtyFiles?.Count ?? 0) == 0 && (manifest.DeletedFiles?.Count ?? 0) == 0))
        {
            if (_useJson)
                WriteJsonResult("impact", new { affected = new List<object>(), provenance = ProvenanceIndexerObserved, fieldProvenance = new { affected = ProvenanceIndexerObserved }, tiersAvailable = false });
            else
                Console.WriteLine("[]");
            return;
        }

        Dictionary<string, SymbolRecord>? fqnToSymbol = null;
        Dictionary<string, string>? fqnToProject = null;
        Dictionary<string, List<string>>? incomingEdges = null;
        var tiersAvailable = false;

        try
        {
            var solution = await LoadSolutionAsync();
            var snapshot = await BuildDiscoverySnapshotAsync(solution);

            fqnToSymbol = snapshot.Symbols.ToDictionary(s => s.MetadataName, s => s);
            fqnToProject = GetSymbolToProjectMap(snapshot);
            incomingEdges = snapshot.IncomingEdges;
            tiersAvailable = true;
        }
        catch
        {
            tiersAvailable = false;
        }

        var affectedFiles = new HashSet<string>(
            (manifest.DirtyFiles ?? []).Concat(manifest.DeletedFiles ?? []));
        var semanticFiles = Directory.GetFiles(SemanticDir, "*.semantic.json");
        var results = new List<object>();

        foreach (var sf in semanticFiles)
        {
            string text;
            try { text = await File.ReadAllTextAsync(sf); }
            catch { continue; }

            JsonNode? node;
            try { node = JsonNode.Parse(text); }
            catch { continue; }

            var symbolId = node?["symbolId"]?.GetValue<string>();
            var sourceFile = node?["facts"]?["sourceFile"]?.GetValue<string>();
            if (symbolId == null || sourceFile == null) continue;

            if (affectedFiles.Contains(sourceFile))
            {
                results.Add(BuildImpactEntry(
                    $"{SanitizeId(symbolId)}.semantic.json", "direct", symbolId, ProvenanceCompilerProved, symbolId,
                    fqnToSymbol, fqnToProject, incomingEdges));
                continue;
            }

            var collaborators = node?["interpretation"]?["collaborators"]?.AsArray();
            if (collaborators == null) continue;

            foreach (var c in collaborators)
            {
                var depSymbol = c?["symbol"]?.GetValue<string>();
                if (depSymbol == null) continue;
                var depSanitized = SanitizeId(depSymbol);
                var depPath = Path.Combine(SemanticDir, $"{depSanitized}.semantic.json");
                if (File.Exists(depPath))
                {
                    try
                    {
                        var depText = await File.ReadAllTextAsync(depPath);
                        var depNode = JsonNode.Parse(depText);
                        var depSource = depNode?["facts"]?["sourceFile"]?.GetValue<string>();
                        if (depSource != null && affectedFiles.Contains(depSource))
                        {
                            results.Add(BuildImpactEntry(
                                $"{SanitizeId(symbolId)}.semantic.json", "dependency", depSymbol, ProvenanceCacheSuggests, symbolId,
                                fqnToSymbol, fqnToProject, incomingEdges));
                            break;
                        }
                    }
                    catch { continue; }
                }
            }
        }

        if (_useJson)
            WriteJsonResult("impact", new { affected = results, provenance = ProvenanceIndexerObserved, fieldProvenance = BuildImpactTierFieldProvenance(), tiersAvailable });
        else
        {
            var output = JsonSerializer.Serialize(results, JsonOptions);
            Console.WriteLine(output);
        }
    }

    private static async Task VerifyFactsAsync()
    {
        var solution = await LoadSolutionAsync();

        var semanticFiles = Directory.GetFiles(SemanticDir, "*.semantic.json");
        var verified = 0;
        var mismatches = new List<object>();
        var unresolvedCollaborators = new List<object>();

        foreach (var sf in semanticFiles)
        {
            var text = await File.ReadAllTextAsync(sf);
            var node = JsonNode.Parse(text)?.AsObject();
            if (node == null) continue;

            var symbolId = node["symbolId"]?.GetValue<string>();
            if (string.IsNullOrEmpty(symbolId)) continue;

            var facts = node["facts"]?.AsObject();
            var collaborators = node["interpretation"]?["collaborators"]?.AsArray();

            var cachedNamespace = facts?["namespace"]?.GetValue<string>();
            var cachedImplements = facts?["implements"]?.GetValue<string>();
            var cachedSourceFile = facts?["sourceFile"]?.GetValue<string>();

            var (symbol, _) = await FindTypeSymbolAsync(solution, symbolId);

            if (symbol == null)
            {
                mismatches.Add(new { symbolId, field = "symbolId", cached = symbolId, actual = "not_found" });
                if (node["status"]?.GetValue<string>() != "stale")
                {
                    node["status"] = "stale";
                    node["staleReason"] = "symbol_missing";
                    var json = node.ToJsonString(JsonOptions);
                    await AtomicWriteAsync(sf, json);
                    WriteProgress($"  Semantic entry flagged stale (symbol_missing): {symbolId}");
                }
                continue;
            }

            var actualNamespace = symbol.ContainingNamespace?.ToDisplayString() ?? "";
            var actualImplements = string.Join(", ", symbol.AllInterfaces
                .Select(i => i.ToDisplayString())
                .OrderBy(x => x));
            var actualSourceFile = GetRelativePath(symbol.Locations.FirstOrDefault()?.SourceTree?.FilePath ?? "");

            var hasMismatch = false;

            if (cachedNamespace != actualNamespace)
            {
                mismatches.Add(new { symbolId, field = "namespace", cached = cachedNamespace, actual = actualNamespace });
                hasMismatch = true;
            }
            if (cachedImplements != actualImplements)
            {
                mismatches.Add(new { symbolId, field = "implements", cached = cachedImplements, actual = actualImplements });
                hasMismatch = true;
            }
            if (cachedSourceFile != actualSourceFile)
            {
                mismatches.Add(new { symbolId, field = "sourceFile", cached = cachedSourceFile, actual = actualSourceFile });
                hasMismatch = true;
            }

            if (hasMismatch)
            {
                if (node["status"]?.GetValue<string>() != "stale")
                {
                    node["status"] = "stale";
                    node["staleReason"] = "facts_mismatch";
                    var json = node.ToJsonString(JsonOptions);
                    await AtomicWriteAsync(sf, json);
                    WriteProgress($"  Semantic entry flagged stale (facts_mismatch): {symbolId}");
                }
            }
            else
            {
                verified++;
            }

            if (collaborators != null)
            {
                foreach (var c in collaborators)
                {
                    var collabSymbol = c?["symbol"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(collabSymbol)) continue;

                    var (resolved, _) = await FindTypeSymbolAsync(solution, collabSymbol);
                    if (resolved == null)
                    {
                        unresolvedCollaborators.Add(new
                        {
                            symbolId,
                            collaborator = collabSymbol,
                            provenance = "cache_suggests_unverified"
                        });
                    }
                }
            }
        }

        WriteJsonResult("verify-facts", new
        {
            verified,
            mismatches,
            unresolvedCollaborators,
            provenance = ProvenanceCompilerProved
        });
    }

    private static async Task AtomicWriteAsync(string path, string content)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static async Task<List<DiagnosticEntry>> SaveBaselineAsync(Solution solution, string path)
    {
        var diagnostics = await CollectDiagnosticsAsync(solution);

        var baseline = new DiagnosticBaseline(
            SchemaVersion: VersionConstants.OutputSchemaVersion,
            CapturedAtUtc: DateTime.UtcNow.ToString("O"),
            SolutionPath: SolutionPath,
            Diagnostics: diagnostics
        );

        var json = JsonSerializer.Serialize(baseline, JsonOptions);
        await AtomicWriteAsync(path, json);

        WriteProgress($"  Baseline saved: {diagnostics.Count} diagnostics");
        return diagnostics;
    }

    private static async Task<DiagnosticBaseline> LoadBaselineAsync(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException("Baseline file not found. Run --mode=check first.");

        var text = await File.ReadAllTextAsync(path);
        var baseline = JsonSerializer.Deserialize<DiagnosticBaseline>(text, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize baseline file.");

        if (baseline.SchemaVersion != VersionConstants.OutputSchemaVersion)
            throw new InvalidOperationException($"Schema version mismatch: expected {VersionConstants.OutputSchemaVersion}, got {baseline.SchemaVersion}");

        return baseline;
    }

    private static (List<DiagnosticEntry> added, List<DiagnosticEntry> removed) DiffDiagnostics(
        DiagnosticBaseline baseline, List<DiagnosticEntry> current)
    {
        var baselineKeys = new HashSet<string>(
            baseline.Diagnostics.Select(d => $"{d.Id}|{d.File}|{d.Line}"));
        var currentKeys = new HashSet<string>(
            current.Select(d => $"{d.Id}|{d.File}|{d.Line}"));

        var added = current.Where(d => !baselineKeys.Contains($"{d.Id}|{d.File}|{d.Line}")).ToList();
        var removed = baseline.Diagnostics.Where(d => !currentKeys.Contains($"{d.Id}|{d.File}|{d.Line}")).ToList();

        return (added, removed);
    }

    private static async Task<List<DiagnosticEntry>> CollectDiagnosticsAsync(Solution solution)
    {
        var diagnostics = new List<DiagnosticEntry>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            foreach (var diagnostic in compilation.GetDiagnostics()
                .Where(d => d.Severity >= DiagnosticSeverity.Warning))
            {
                string file = "";
                int line = 0;
                if (diagnostic.Location.Kind != LocationKind.None && diagnostic.Location.SourceTree != null)
                {
                    file = GetRelativePath(diagnostic.Location.SourceTree.FilePath);
                    line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1;
                }

                diagnostics.Add(new DiagnosticEntry(
                    ProjectName: project.Name,
                    Id: diagnostic.Id,
                    Severity: diagnostic.Severity.ToString(),
                    Message: diagnostic.GetMessage(),
                    File: file,
                    Line: line
                ));
            }
        }
        return diagnostics;
    }

    private static async Task<List<DiagnosticEntry>> CompileAndCollectDiagnosticsAsync(Solution solution)
    {
        return await CollectDiagnosticsAsync(solution);
    }

    private static DiagnosticBaseline EnsureBaselineFreshness()
    {
        var baselinePath = Path.Combine(CodeAuditDir, "baseline-diagnostics.json");
        if (!File.Exists(baselinePath))
        {
            WriteJsonResult("error", new
            {
                error = "stale_baseline",
                message = "Run --mode=check first",
                baselinePath = GetRelativePath(baselinePath)
            });
            Environment.Exit(1);
        }

        var baseline = LoadBaselineAsync(baselinePath).GetAwaiter().GetResult();
        var capturedAt = DateTime.Parse(baseline.CapturedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind);
        if (DateTime.UtcNow - capturedAt > TimeSpan.FromHours(24))
        {
            WriteJsonResult("error", new
            {
                error = "stale_baseline",
                message = "Baseline is older than 24 hours. Run --mode=check again",
                baselinePath = GetRelativePath(baselinePath),
                capturedAtUtc = baseline.CapturedAtUtc
            });
            Environment.Exit(1);
        }

        return baseline;
    }

    private static async Task<List<ResolveCandidate>> ResolveSymbolAsync(Solution solution, string partialName)
    {
        var candidates = new List<ResolveCandidate>();
        var seen = new HashSet<string>();

        await foreach (var (project, compilation) in CompilationHelper.GetAllAsync(solution))
        {

            foreach (var document in project.Documents)
            {
                var filePath = document.FilePath ?? "";
                if (IsGeneratedFile(filePath)) continue;

                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null) continue;

                var root = await syntaxTree.GetRootAsync();
                var model = compilation.GetSemanticModel(syntaxTree);

                foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol namedSymbol)
                        continue;

                    var displayString = namedSymbol.ToDisplayString();
                    if (!seen.Add(displayString)) continue;

                    int score;
                    string scoreReason;

                    if (displayString == partialName)
                    {
                        score = 100;
                        scoreReason = "exact metadataName match";
                    }
                    else if (namedSymbol.Name == partialName)
                    {
                        score = 80;
                        scoreReason = "simple name match ignoring namespace";
                    }
                    else if (displayString.EndsWith(partialName))
                    {
                        score = 60;
                        scoreReason = "metadataName ends with partialName";
                    }
                    else if (displayString.Contains(partialName))
                    {
                        score = 40;
                        scoreReason = "metadataName contains partialName";
                    }
                    else
                    {
                        continue;
                    }

                    var (kind, _, _) = ClassifyType(namedSymbol, compilation);

                    var line = namedSymbol.Locations
                        .Where(loc => loc.IsInSource)
                        .Select(loc => loc.GetLineSpan().StartLinePosition.Line + 1)
                        .FirstOrDefault();

                    candidates.Add(new ResolveCandidate(
                        MetadataName: displayString,
                        Kind: kind,
                        Project: project.Name,
                        File: GetRelativePath(filePath),
                        Line: line,
                        Score: score,
                        ScoreReason: scoreReason
                    ));
                }
            }
        }

        return candidates.OrderByDescending(c => c.Score).Take(10).ToList();
    }

    private static List<object> GetSimulateBlindSpots()
    {
        return GetBlindSpotsForContext("simulate-delete");
    }

    private static List<object> GetRenameBlindSpots()
    {
        return GetBlindSpotsForContext("simulate-rename");
    }

    private static List<object> ScanCacheImpact(string fqn)
    {
        var results = new List<object>();
        if (!Directory.Exists(CodeAuditDir)) return results;

        foreach (var file in Directory.GetFiles(CodeAuditDir, "*.json", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);
            if (fileName == "baseline-diagnostics.json" || fileName == "dirty-files.json")
                continue;

            try
            {
                var text = File.ReadAllText(file);
                var node = JsonNode.Parse(text);
                if (node == null) continue;

                var foundFields = new List<string>();
                WalkJsonForValue(node, fqn, "", foundFields);

                if (foundFields.Count > 0)
                {
                    results.Add(new { file = GetRelativePath(file), fields = foundFields });
                }
            }
            catch {  }
        }

        return results;
    }

    private static void WalkJsonForValue(JsonNode? node, string target, string path, List<string> foundFields)
    {
        if (node == null) return;

        if (node is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var str) && str == target)
                foundFields.Add(path.TrimStart('.'));
            return;
        }

        if (node is JsonObject obj)
        {
            foreach (var kvp in obj)
            {
                WalkJsonForValue(kvp.Value, target, path + "." + kvp.Key, foundFields);
            }
            return;
        }

        if (node is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                WalkJsonForValue(arr[i], target, $"{path}[{i}]", foundFields);
            }
        }
    }

    private static async Task<(Solution solution, DiagnosticBaseline baseline, INamedTypeSymbol? symbol, Compilation? compilation)> RunSimulationPipelineAsync(string fqn)
    {
        var solution = await LoadSolutionAsync();
        var baseline = EnsureBaselineFreshness();
        var (symbol, compilation) = await FindTypeSymbolAsync(solution, fqn);
        return (solution, baseline, symbol, compilation);
    }

    private static async Task HandleSimulateDeleteAsync(string fqn)
    {
        var (solution, baseline, symbol, compilation) = await RunSimulationPipelineAsync(fqn);
        if (symbol == null)
        {
            WriteJsonResult("simulate-delete", new
            {
                command = "simulate-delete",
                symbol = fqn,
                resolved = false
            });
            return;
        }

        var referencedSymbols = await SymbolFinder.FindReferencesAsync(symbol, solution);
        var allRefs = referencedSymbols.SelectMany(r => r.Locations).ToList();
        var referenceSites = allRefs.Select(r => new
        {
            file = GetRelativePath(r.Location.SourceTree?.FilePath),
            line = r.Location.GetLineSpan().StartLinePosition.Line + 1,
            project = r.Document.Project.Name
        }).ToList();
        var referenceSiteCount = referenceSites.Count;

        bool externallyExposed = false;
        string? externallyExposedRule = null;

        if (InheritsFrom(symbol, "Microsoft.AspNetCore.Mvc.ControllerBase"))
        {
            externallyExposed = true;
            externallyExposedRule = "inherits from ControllerBase";
        }
        else if (symbol.GetAttributes().Any(a =>
            a.AttributeClass?.Name == "ApiControllerAttribute" &&
            a.AttributeClass.ContainingNamespace?.ToDisplayString().Contains("Mvc") == true))
        {
            externallyExposed = true;
            externallyExposedRule = "has ApiController attribute";
        }

        var docIds = symbol.Locations
            .Where(l => l.IsInSource)
            .Select(l => solution.GetDocument(l.SourceTree))
            .Where(d => d != null)
            .Select(d => d!.Id)
            .Distinct()
            .ToImmutableArray();

        var removedSolution = solution.RemoveDocuments(docIds);
        var currentDiagnostics = await CollectDiagnosticsAsync(removedSolution);
        var (addedDiagnostics, _) = DiffDiagnostics(baseline, currentDiagnostics);

        var cacheImpact = ScanCacheImpact(fqn);

        string verdict;
        string verdictRule;

        if (externallyExposed)
        {
            verdict = "unsafe";
            verdictRule = "symbol is externally exposed";
        }
        else if (addedDiagnostics.Count > 0)
        {
            verdict = "introduces_errors";
            verdictRule = $"removal introduces {addedDiagnostics.Count} new diagnostic(s)";
        }
        else if (referenceSiteCount == 0)
        {
            verdict = "no_csharp_references";
            verdictRule = "no C# references found in solution";
        }
        else
        {
            verdict = "no_new_diagnostics";
            verdictRule = $"{referenceSiteCount} reference(s) exist but no new diagnostics introduced";
        }

        WriteJsonResult("simulate-delete", new
        {
            command = "simulate-delete",
            symbol = fqn,
            resolved = true,
            externallyExposed,
            externallyExposedRule,
            referenceSiteCount,
            referenceSites,
            referenceSiteProvenance = "compiler_proved",
            newDiagnostics = addedDiagnostics,
            newDiagnosticsProvenance = "compiler_proved",
            cacheImpact,
            blindSpots = GetSimulateBlindSpots(),
            verdict,
            verdictRule
        });
    }

    private static async Task HandleSimulateRenameAsync(string fqn, string newName)
    {
        var (solution, baseline, symbol, compilation) = await RunSimulationPipelineAsync(fqn);
        if (symbol == null)
        {
            WriteJsonResult("simulate-rename", new
            {
                command = "simulate-rename",
                symbol = fqn,
                newName,
                resolved = false
            });
            return;
        }

        var ns = symbol.ContainingNamespace;
        string newFqn = ns != null && !ns.IsGlobalNamespace
            ? ns.ToDisplayString() + "." + newName
            : newName;

        var (collidingSymbol, _) = await FindTypeSymbolAsync(solution, newFqn);
        if (collidingSymbol != null)
        {
            WriteJsonResult("simulate-rename", new
            {
                command = "simulate-rename",
                symbol = fqn,
                newName,
                resolved = true,
                verdict = "collision",
                collidingSymbol = newFqn
            });
            return;
        }

        var renamedSolution = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), newName);

        var changedDocuments = new List<object>();
        var projectChanges = renamedSolution.GetChanges(solution).GetProjectChanges();
        foreach (var projectChange in projectChanges)
        {
            foreach (var docId in projectChange.GetChangedDocuments())
            {
                var oldDoc = solution.GetDocument(docId);
                var newDoc = renamedSolution.GetDocument(docId);
                if (oldDoc == null || newDoc == null) continue;

                var oldTree = await oldDoc.GetSyntaxTreeAsync();
                var newTree = await newDoc.GetSyntaxTreeAsync();
                if (oldTree == null || newTree == null) continue;

                var oldRoot = await oldTree.GetRootAsync();
                var newRoot = await newTree.GetRootAsync();

                var oldText = oldRoot.ToFullString();
                var newText = newRoot.ToFullString();

                var oldLines = oldText.Split('\n').Length;
                var newLines = newText.Split('\n').Length;

                changedDocuments.Add(new
                {
                    file = GetRelativePath(oldDoc.FilePath),
                    addedLines = Math.Max(0, newLines - oldLines),
                    removedLines = Math.Max(0, oldLines - newLines)
                });
            }
        }

        var currentDiagnostics = await CollectDiagnosticsAsync(renamedSolution);
        var (addedDiagnostics, _) = DiffDiagnostics(baseline, currentDiagnostics);

        var cacheImpact = ScanCacheImpact(fqn);

        string verdict;
        string verdictRule;

        if (addedDiagnostics.Count > 0)
        {
            verdict = "introduces_errors";
            verdictRule = $"rename introduces {addedDiagnostics.Count} new diagnostic(s)";
        }
        else
        {
            verdict = "no_new_diagnostics";
            verdictRule = "no new diagnostics introduced by rename";
        }

        WriteJsonResult("simulate-rename", new
        {
            command = "simulate-rename",
            symbol = fqn,
            newName,
            newFqn,
            resolved = true,
            changedDocumentCount = changedDocuments.Count,
            changedDocuments,
            newDiagnostics = addedDiagnostics,
            newDiagnosticsProvenance = "compiler_proved",
            cacheImpact,
            blindSpots = GetRenameBlindSpots(),
            verdict,
            verdictRule
        });
    }

    private static async Task HandleSimulateMoveAsync(string fqn, string targetNamespace, string? targetProjectArg)
    {
        var (solution, baseline, symbol, compilation) = await RunSimulationPipelineAsync(fqn);
        if (symbol == null)
        {
            WriteJsonResult("simulate-move", new
            {
                command = "simulate-move",
                symbol = fqn,
                targetNamespace,
                targetProject = targetProjectArg,
                resolved = false
            });
            return;
        }

        var simpleName = symbol.Name;
        var sourceNamespace = symbol.ContainingNamespace?.ToDisplayString() ?? "";
        if (symbol.ContainingNamespace?.IsGlobalNamespace == true)
            sourceNamespace = "<global>";

        var sourceDoc = symbol.Locations
            .Where(l => l.IsInSource)
            .Select(l => solution.GetDocument(l.SourceTree))
            .FirstOrDefault(d => d != null);
        var sourceProject = sourceDoc?.Project.Name ?? "";

        var sourceDocIds = symbol.Locations
            .Where(l => l.IsInSource)
            .Select(l => solution.GetDocument(l.SourceTree))
            .Where(d => d != null)
            .Select(d => d!.Id)
            .Distinct()
            .ToHashSet();

        var isGlobalTarget = string.IsNullOrEmpty(targetNamespace) || targetNamespace == "<global>";
        var newFqn = isGlobalTarget ? simpleName : targetNamespace + "." + simpleName;

        string? resolvedTargetProject;

        if (!string.IsNullOrEmpty(targetProjectArg))
        {
            resolvedTargetProject = targetProjectArg;
        }
        else
        {

            var matchedProjects = new List<(string projectName, string rootNamespace)>();
            foreach (var project in solution.Projects)
            {
                var rootNs = project.DefaultNamespace;
                if (string.IsNullOrEmpty(rootNs))
                    rootNs = project.Name;
                if (string.IsNullOrEmpty(rootNs))
                    continue;

                if (targetNamespace == rootNs || targetNamespace.StartsWith(rootNs + "."))
                {
                    matchedProjects.Add((project.Name, rootNs));
                }
            }

            if (matchedProjects.Count == 0)
            {
                resolvedTargetProject = null;
            }
            else if (matchedProjects.Count == 1)
            {
                resolvedTargetProject = matchedProjects[0].projectName;
            }
            else
            {
                var distinctNames = matchedProjects.Select(m => m.projectName).Distinct().ToList();
                WriteJsonResult("simulate-move", new
                {
                    error = "ambiguous_target_project",
                    candidates = distinctNames
                });
                Environment.Exit(1);
                return;
            }
        }

        if (resolvedTargetProject == null)
        {
            WriteJsonResult("simulate-move", new
            {
                error = "target_project_not_found",
                message = $"No project found matching namespace '{targetNamespace}'"
            });
            Environment.Exit(1);
            return;
        }

        if (sourceNamespace == targetNamespace && sourceProject == resolvedTargetProject)
        {
            WriteJsonResult("simulate-move", new
            {
                error = "no_op_move",
                message = "target namespace and project are identical to source"
            });
            return;
        }

        bool isCrossProject = !string.IsNullOrEmpty(sourceProject) &&
                              sourceProject != resolvedTargetProject;

        var snapshot = await BuildDiscoverySnapshotAsync(solution);
        var projectGraph = BuildProjectReferenceGraph(solution);

        var fqnToProject = new Dictionary<string, string>();
        foreach (var s in snapshot.Symbols)
            fqnToProject[s.MetadataName] = s.Project;

        string? verdict = null;
        string? verdictRule = null;

        bool collision;
        string? collidingSymbolFqn;
        {

            var (collidingType, _) = await FindTypeSymbolAsync(solution, newFqn);
            if (collidingType != null && !SymbolEqualityComparer.Default.Equals(collidingType, symbol))
            {
                collision = true;
                collidingSymbolFqn = newFqn;
            }
            else
            {

                var snapshotCollision = snapshot.Symbols.FirstOrDefault(s =>
                    s.Namespace == targetNamespace &&
                    GetSimpleName(s.MetadataName) == simpleName &&
                    s.MetadataName != fqn);
                if (snapshotCollision != null)
                {
                    collision = true;
                    collidingSymbolFqn = snapshotCollision.MetadataName;
                }
                else
                {
                    collision = false;
                    collidingSymbolFqn = null;
                }
            }
        }

        var collisionProof = new
        {
            collision,
            collidingSymbol = collidingSymbolFqn,
            provenance = ProvenanceCompilerProved
        };

        if (collision)
        {
            verdict = "collision";
            verdictRule = "target namespace already contains a type with the same simple name";

            WriteJsonResult("simulate-move", new
            {
                command = "simulate-move",
                symbol = fqn,
                targetNamespace,
                targetProject = resolvedTargetProject,
                newFqn,
                resolved = true,
                isCrossProject,
                collisionProof,
                dependencyVisibilityProof = new
                {
                    missingProjectReferences = (object?)null,
                    wouldIntroduceCycle = (bool?)null,
                    provenance = ProvenanceCompilerProved,
                    status = "not_checked"
                },
                referencerVisibilityProof = new
                {
                    visibilityGaps = (object?)null,
                    wouldIntroduceCycle = (bool?)null,
                    callerSetProvenance = ProvenanceIndexerObserved,
                    cycleCheckProvenance = ProvenanceCompilerProved,
                    note = "caller set is structural; callers via interface polymorphism may be absent",
                    status = "not_checked"
                },
                blindSpots = GetMoveBlindSpots(),
                verdict,
                verdictRule
            });
            return;
        }

        var missingProjectReferences = new List<object>();
        bool depWouldIntroduceCycle = false;

        if (!isCrossProject)
        {

            depWouldIntroduceCycle = false;
        }
        else
        {
            var externalTypeFqns = CollectExternalTypeFqns(symbol);

            var existingRefs = projectGraph.TryGetValue(resolvedTargetProject!, out var refs)
                ? refs
                : new HashSet<string>();

            foreach (var extFqn in externalTypeFqns)
            {
                if (!fqnToProject.TryGetValue(extFqn, out var depProject))
                    continue;
                if (depProject == resolvedTargetProject)
                    continue;
                if (existingRefs.Contains(depProject))
                    continue;

                var wouldCycle = HasPathInProjectGraph(projectGraph, depProject, resolvedTargetProject!);

                missingProjectReferences.Add(new
                {
                    dependencyType = extFqn,
                    dependencyProject = depProject,
                    wouldIntroduceCycle = wouldCycle
                });

                if (wouldCycle)
                    depWouldIntroduceCycle = true;
            }
        }

        var dependencyVisibilityProof = new
        {
            missingProjectReferences,
            wouldIntroduceCycle = depWouldIntroduceCycle,
            provenance = ProvenanceCompilerProved
        };

        if (depWouldIntroduceCycle)
        {
            verdict = "dependency_not_visible";
            verdictRule = "moving to target project would require a new project reference that creates a cycle in the dependency graph";

            WriteJsonResult("simulate-move", new
            {
                command = "simulate-move",
                symbol = fqn,
                targetNamespace,
                targetProject = resolvedTargetProject,
                newFqn,
                resolved = true,
                isCrossProject,
                collisionProof,
                dependencyVisibilityProof,
                referencerVisibilityProof = new
                {
                    visibilityGaps = (object?)null,
                    wouldIntroduceCycle = (bool?)null,
                    callerSetProvenance = ProvenanceIndexerObserved,
                    cycleCheckProvenance = ProvenanceCompilerProved,
                    note = "caller set is structural; callers via interface polymorphism may be absent",
                    status = "not_checked"
                },
                blindSpots = GetMoveBlindSpots(),
                verdict,
                verdictRule
            });
            return;
        }

        var visibilityGaps = new List<object>();
        bool refWouldIntroduceCycle = false;

        {

            var (callerFqns, _) = GetIncomingEdges(snapshot, fqn);

            if (!isCrossProject)
            {

                refWouldIntroduceCycle = false;
            }
            else
            {

                var sourceRefs = projectGraph.TryGetValue(sourceProject, out var sRefs)
                    ? sRefs
                    : new HashSet<string>();
                bool hasRefToTarget = sourceRefs.Contains(resolvedTargetProject!);

                if (!hasRefToTarget)
                {

                    refWouldIntroduceCycle = HasPathInProjectGraph(projectGraph, resolvedTargetProject!, sourceProject);

                    if (!refWouldIntroduceCycle)
                    {

                        foreach (var callerFqn in callerFqns)
                        {
                            if (fqnToProject.TryGetValue(callerFqn, out var callerProject) &&
                                callerProject == sourceProject)
                            {
                                visibilityGaps.Add(new
                                {
                                    callerSymbol = callerFqn,
                                    callerProject = sourceProject
                                });
                            }
                        }
                    }
                }
            }
        }

        var referencerVisibilityProof = new
        {
            visibilityGaps,
            wouldIntroduceCycle = refWouldIntroduceCycle,
            callerSetProvenance = ProvenanceIndexerObserved,
            cycleCheckProvenance = ProvenanceCompilerProved,
            note = "caller set is structural; callers via interface polymorphism may be absent"
        };

        if (refWouldIntroduceCycle)
        {
            verdict = "referencer_not_visible";
            verdictRule = "callers would lose visibility after the move, and the project reference change required to restore it would introduce a cycle";

            WriteJsonResult("simulate-move", new
            {
                command = "simulate-move",
                symbol = fqn,
                targetNamespace,
                targetProject = resolvedTargetProject,
                newFqn,
                resolved = true,
                isCrossProject,
                collisionProof,
                dependencyVisibilityProof,
                referencerVisibilityProof,
                blindSpots = GetMoveBlindSpots(),
                verdict,
                verdictRule
            });
            return;
        }

        List<object> changedDocuments = new();
        List<DiagnosticEntry> addedDiagnostics = new();
        List<object>? cacheImpact = null;
        var affectedProjects = new HashSet<string>();

        if (verdict == null)
        {

            (solution, changedDocuments) = await ApplyInMemoryMoveAsync(
                solution, fqn, newFqn, symbol, sourceNamespace, targetNamespace, sourceDocIds);

            foreach (var cd in changedDocuments)
            {
                var file = cd.GetType().GetProperty("file")?.GetValue(cd)?.ToString();
                if (file != null)
                {
                    foreach (var project in solution.Projects)
                    {
                        foreach (var doc in project.Documents)
                        {
                            if (GetRelativePath(doc.FilePath) == file)
                            {
                                affectedProjects.Add(project.Name);
                                break;
                            }
                        }
                    }
                }
            }

            var currentDiagnostics = await CollectDiagnosticsAsync(solution);
            (addedDiagnostics, _) = DiffDiagnostics(baseline, currentDiagnostics);

            cacheImpact = ScanCacheImpactForMove(fqn, newFqn);

            if (addedDiagnostics.Count > 0)
            {
                verdict = "introduces_errors";
                verdictRule = $"recompile introduces {addedDiagnostics.Count} new diagnostic(s)";
            }
            else
            {
                verdict = "no_new_diagnostics";
                verdictRule = "all three proof checks raised no hard stop; recompile introduced no new diagnostics";
            }
        }

        WriteJsonResult("simulate-move", new
        {
            command = "simulate-move",
            symbol = fqn,
            targetNamespace,
            targetProject = resolvedTargetProject,
            newFqn,
            resolved = true,
            isCrossProject,
            collisionProof,
            dependencyVisibilityProof,
            referencerVisibilityProof,
            changedDocumentCount = changedDocuments.Count,
            changedDocuments,
            newDiagnostics = addedDiagnostics,
            newDiagnosticsProvenance = ProvenanceCompilerProved,
            cacheImpact = cacheImpact ?? new List<object>(),
            blindSpots = GetMoveBlindSpots(),
            verdict,
            verdictRule
        });
    }

    private static Dictionary<string, HashSet<string>> BuildProjectReferenceGraph(Solution solution)
    {
        var projectIdToName = solution.Projects.ToDictionary(p => p.Id, p => p.Name);
        var graph = new Dictionary<string, HashSet<string>>();

        foreach (var project in solution.Projects)
        {
            var refs = new HashSet<string>();
            foreach (var pr in project.ProjectReferences)
            {
                if (projectIdToName.TryGetValue(pr.ProjectId, out var name))
                    refs.Add(name);
            }
            graph[project.Name] = refs;
        }

        return graph;
    }

    private static bool HasPathInProjectGraph(
        Dictionary<string, HashSet<string>> graph,
        string fromProject,
        string toProject)
    {
        if (fromProject == toProject)
            return true;

        var visited = new HashSet<string>();
        var stack = new Stack<string>();
        stack.Push(fromProject);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == toProject)
                return true;
            if (!visited.Add(current))
                continue;

            if (graph.TryGetValue(current, out var neighbors))
            {
                foreach (var neighbor in neighbors)
                {
                    if (!visited.Contains(neighbor))
                        stack.Push(neighbor);
                }
            }
        }

        return false;
    }

    private static string GetSimpleName(string metadataName)
    {
        var lastDot = metadataName.LastIndexOf('.');
        var simple = lastDot >= 0 ? metadataName[(lastDot + 1)..] : metadataName;
        var backtick = simple.IndexOf('`');
        return backtick >= 0 ? simple[..backtick] : simple;
    }

    private static async Task<(Solution solution, List<object> changedDocuments)> ApplyInMemoryMoveAsync(
        Solution solution,
        string oldFqn,
        string newFqn,
        INamedTypeSymbol symbol,
        string sourceNamespace,
        string targetNamespace,
        HashSet<DocumentId> sourceDocIds)
    {

        if (sourceNamespace == targetNamespace)
            return (solution, new List<object>());

        string? tempName = null;
        for (var attempt = 0; attempt < 5 && tempName == null; attempt++)
        {
            var candidate = symbol.Name + "_" + Guid.NewGuid().ToString("N")[..8];
            var (collidingTemp, _) = await FindTypeSymbolAsync(solution, $"{sourceNamespace}.{candidate}");
            if (collidingTemp == null)
                tempName = candidate;
        }
        tempName ??= symbol.Name + "_" + Guid.NewGuid().ToString("N")[..8];

        var tempSolution = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), tempName);

        var midSolution = tempSolution;

        var oldTempQualifiedName = $"{sourceNamespace}.{tempName}";
        var newTempQualifiedName = $"{targetNamespace}.{tempName}";

        var allDocIds = midSolution.Projects
            .SelectMany(p => p.Documents.Select(d => d.Id))
            .Distinct()
            .ToList();

        foreach (var docId in allDocIds)
        {
            var doc = midSolution.GetDocument(docId);
            if (doc == null) continue;
            var root = await doc.GetSyntaxRootAsync();
            if (root == null) continue;

            var newRoot = root;

            if (sourceDocIds.Contains(docId))
                newRoot = ChangeNamespaceDeclarations(newRoot, sourceNamespace, targetNamespace);

            newRoot = UpdateUsingDirectives(newRoot, sourceNamespace, targetNamespace);

            newRoot = ReplaceQualifiedName(newRoot, oldTempQualifiedName, newTempQualifiedName);

            if (newRoot != root)
                midSolution = midSolution.WithDocumentSyntaxRoot(docId, newRoot);
        }

        var (movedSymbol, _) = await FindTypeSymbolAsync(midSolution, targetNamespace + "." + tempName);
        if (movedSymbol == null)
        {
            Console.Error.WriteLine(
                $"Warning: could not re-resolve '{targetNamespace}.{tempName}' after namespace move; returning without final rename.");
            return (midSolution, new List<object>());
        }

        var finalSolution = await Renamer.RenameSymbolAsync(midSolution, movedSymbol, new SymbolRenameOptions(), symbol.Name);

        var changedDocuments = new List<object>();
        var projectChanges = finalSolution.GetChanges(solution).GetProjectChanges();
        foreach (var projectChange in projectChanges)
        {
            foreach (var docId in projectChange.GetChangedDocuments())
            {
                var oldDoc = solution.GetDocument(docId);
                var newDoc = finalSolution.GetDocument(docId);
                if (oldDoc == null || newDoc == null) continue;

                var oldTree = await oldDoc.GetSyntaxTreeAsync();
                var newTree = await newDoc.GetSyntaxTreeAsync();
                if (oldTree == null || newTree == null) continue;

                var oldRoot = await oldTree.GetRootAsync();
                var newRoot = await newTree.GetRootAsync();

                var oldText = oldRoot.ToFullString();
                var newText = newRoot.ToFullString();

                var oldLines = oldText.Split('\n').Length;
                var newLines = newText.Split('\n').Length;

                changedDocuments.Add(new
                {
                    file = GetRelativePath(oldDoc.FilePath),
                    addedLines = Math.Max(0, newLines - oldLines),
                    removedLines = Math.Max(0, oldLines - newLines)
                });
            }
        }

        return (finalSolution, changedDocuments);
    }

    private static SyntaxNode ChangeNamespaceDeclarations(SyntaxNode root, string oldNs, string newNs)
    {
        var rewritten = root;

        var fileScoped = rewritten.DescendantNodes()
            .OfType<FileScopedNamespaceDeclarationSyntax>()
            .Where(ns => ns.Name.ToString() == oldNs)
            .ToList();
        foreach (var ns in fileScoped)
        {
            var newName = SyntaxFactory.ParseName(newNs)
                .WithTriviaFrom(ns.Name);
            rewritten = rewritten.ReplaceNode(ns, ns.WithName(newName));
        }

        var blockScoped = rewritten.DescendantNodes()
            .OfType<NamespaceDeclarationSyntax>()
            .Where(ns => ns.Name.ToString() == oldNs)
            .ToList();
        foreach (var ns in blockScoped)
        {
            var newName = SyntaxFactory.ParseName(newNs)
                .WithTriviaFrom(ns.Name);
            rewritten = rewritten.ReplaceNode(ns, ns.WithName(newName));
        }

        return rewritten;
    }

    private static SyntaxNode UpdateUsingDirectives(SyntaxNode root, string oldNs, string newNs)
    {
        var usings = root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Where(u => u.Name != null && u.Name.ToString() == oldNs)
            .ToList();

        var rewritten = root;
        foreach (var u in usings)
        {
            var newName = SyntaxFactory.ParseName(newNs)
                .WithTriviaFrom(u.Name!);
            rewritten = rewritten.ReplaceNode(u, u.WithName(newName));
        }

        return rewritten;
    }

    private static SyntaxNode ReplaceQualifiedName(SyntaxNode root, string oldQualifiedName, string newQualifiedName)
    {
        var rewritten = root;

        var qualifiedRefs = rewritten.DescendantNodes()
            .OfType<QualifiedNameSyntax>()
            .Where(qn => qn.ToString() == oldQualifiedName)
            .ToList();
        foreach (var qn in qualifiedRefs)
        {
            var newName = SyntaxFactory.ParseName(newQualifiedName)
                .WithTriviaFrom(qn);
            rewritten = rewritten.ReplaceNode(qn, newName);
        }

        return rewritten;
    }

    private static List<object> ScanCacheImpactForMove(string oldFqn, string newFqn)
    {
        var oldHits = ScanCacheImpact(oldFqn);
        var newHits = ScanCacheImpact(newFqn);

        var seenFiles = new HashSet<string>();
        var merged = new List<object>();

        foreach (var hit in oldHits)
        {
            var file = hit.GetType().GetProperty("file")?.GetValue(hit)?.ToString();
            if (file != null && seenFiles.Add(file))
                merged.Add(hit);
        }

        foreach (var hit in newHits)
        {
            var file = hit.GetType().GetProperty("file")?.GetValue(hit)?.ToString();
            if (file != null && seenFiles.Add(file))
                merged.Add(hit);
        }

        return merged;
    }

    private static List<object> GetMoveBlindSpots()
    {
        return GetBlindSpotsForContext("simulate-move");
    }

    private static async Task HandlePruneAsync()
    {
        var solution = await LoadSolutionAsync();
        var files = Directory.GetFiles(CodeAuditDir, "*.json", SearchOption.AllDirectories)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name != "baseline-diagnostics.json" && name != "dirty-files.json";
            })
            .ToList();

        var entries = new List<object>();
        var missingCount = 0;
        var degradedCount = 0;
        var okCount = 0;

        foreach (var file in files)
        {
            try
            {
                var relFile = GetRelativePath(file);
                var text = await File.ReadAllTextAsync(file);
                var node = JsonNode.Parse(text);
                if (node == null) continue;

                var symbolValue = node["symbol"]?.GetValue<string>();
                if (string.IsNullOrEmpty(symbolValue))
                {

                    symbolValue = node["symbolId"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(symbolValue))
                        continue;
                }

                var (typedSymbol, _) = await FindTypeSymbolAsync(solution, symbolValue);
                if (typedSymbol == null)
                {
                    entries.Add(new
                    {
                        file = relFile,
                        symbol = symbolValue,
                        status = "missing",
                        action = "review_and_remove"
                    });
                    missingCount++;
                    continue;
                }

                var facts = node["facts"]?.AsObject();
                if (facts != null)
                {
                    var cachedFile = facts["sourceFile"]?.GetValue<string>();
                    var cachedLine = facts["line"]?.GetValue<int>();

                    if (cachedFile != null || cachedLine != null)
                    {
                        var currentFile = GetRelativePath(typedSymbol.Locations
                            .FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath ?? "");
                        var currentLine = typedSymbol.Locations
                            .Where(l => l.IsInSource)
                            .Select(l => l.GetLineSpan().StartLinePosition.Line + 1)
                            .FirstOrDefault();

                        bool fileMismatch = cachedFile != null && cachedFile != currentFile;
                        bool lineMismatch = cachedLine != null && Math.Abs(cachedLine.Value - currentLine) > 5;

                        if (fileMismatch || lineMismatch)
                        {
                            entries.Add(new
                            {
                                file = relFile,
                                symbol = symbolValue,
                                status = "degraded",
                                action = "review_and_update"
                            });
                            degradedCount++;
                            continue;
                        }
                    }
                }

                entries.Add(new
                {
                    file = relFile,
                    symbol = symbolValue,
                    status = "ok",
                    action = (string?)null
                });
                okCount++;
            }
            catch {  }
        }

        WriteJsonResult("prune", new
        {
            checkedCount = entries.Count,
            missingCount,
            degradedCount,
            okCount,
            entries
        });
    }

    private static async Task ShowStatusAsync()
    {
        // ── Build current workspace info ─────────────────────────
        WorkspaceInfo? workspace = null;
        string? loadError = null;
        try
        {
            var solution = await LoadSolutionAsync();
            workspace = new WorkspaceInfo(solution, GitRoot);
        }
        catch (Exception ex)
        {
            loadError = ex.Message;
        }

        // ── Try to load stored snapshot ──────────────────────────
        var snapshotPath = Path.Combine(CodeAuditDir, "snapshot.json");
        SnapshotManifest? stored = null;
        string? snapshotLoadError = null;
        if (File.Exists(snapshotPath))
        {
            try
            {
                stored = SnapshotManifest.Load(snapshotPath);
            }
            catch (Exception ex)
            {
                snapshotLoadError = ex.Message;
            }
        }

        // ── Run freshness check ──────────────────────────────────
        WorkspaceFreshness.FreshnessResult? freshness = null;
        if (workspace != null)
            freshness = WorkspaceFreshness.CheckFreshness(workspace, stored);

        var statusLabel = freshness == null
            ? "error"
            : stored == null
                ? "never_indexed"
                : freshness.IsFresh
                    ? "fresh"
                    : "stale";

        // ── JSON output ──────────────────────────────────────────
        if (_useJson)
        {
            var mismatchList = freshness?.Mismatches.Select(m => new
            {
                kind = m.Kind.ToString(),
                description = m.Description,
                document = m.Document?.ToString(),
                detail = m.Detail
            }).ToList();

            object result = new
            {
                status = statusLabel,
                gitRoot = GitRoot,
                codeAuditDir = CodeAuditDir,
                semanticDir = SemanticDir,
                solutionPath = SolutionPath,
                workspaceId = workspace?.Id.Value,
                snapshotId = stored?.SnapshotId.ToString(),
                mismatchCount = freshness?.Mismatches.Count ?? 0,
                mismatches = mismatchList ?? new List<object>(),
                loadError,
                snapshotLoadError,
                provenance = GetProvenanceIndexerObserved()
            };

            WriteJsonResult("status", result);
            return;
        }

        // ── Text output ──────────────────────────────────────────
        Console.WriteLine($"  Git root: {GitRoot}");
        Console.WriteLine($"  CodeAudit dir: {CodeAuditDir}");
        Console.WriteLine($"  Semantic dir: {SemanticDir}");
        Console.WriteLine($"  Solution: {SolutionPath}");

        if (loadError != null)
        {
            Console.WriteLine($"  ERROR: Solution load failed — {loadError}");
            return;
        }

        if (snapshotLoadError != null)
        {
            Console.WriteLine($"  WARNING: Failed to parse existing snapshot.json — {snapshotLoadError}");
        }

        if (stored == null)
        {
            Console.WriteLine("  Status: Never indexed.");
            return;
        }

        if (freshness!.IsFresh)
        {
            Console.WriteLine("  Status: Fresh.");
        }
        else
        {
            Console.WriteLine($"  Status: Stale — {freshness.Mismatches.Count} mismatch(es):");
            foreach (var m in freshness.Mismatches)
            {
                var docInfo = m.Document != null ? $" [{m.Document}]" : "";
                var detailInfo = m.Detail != null ? $" ({m.Detail})" : "";
                Console.WriteLine($"    - {m.Kind}{docInfo}: {m.Description}{detailInfo}");
            }
        }
    }
}
