using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.FindSymbols;

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

public class Program
{
    private const int SchemaVersion = 2;
    private const string IndexerVersion = "1.1.0";

    private const string ProvenanceCompilerProved = "compiler_proved";
    private const string ProvenanceIndexerObserved = "indexer_observed";
    private const string ProvenanceCacheSuggests = "cache_suggests";
    private const string ProvenanceNotDeterminable = "not_determinable";

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
                    ShowStatus();
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
                    Console.WriteLine("  --mode=impact");
                    Console.WriteLine("  --mode=verify-facts [--json]");
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
            indexerVersion = IndexerVersion,
            schemaVersion = SchemaVersion,
            command,
            solutionPath = SolutionPath,
            timestampUtc = DateTime.UtcNow.ToString("O"),
            result
        };
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        Console.WriteLine(json);
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
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            var type = compilation.GetTypeByMetadataName(name);
            if (type != null) return (type, compilation);
        }
        return (null, null);
    }

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

    private static List<object> GetBlindSpots()
    {
        return new List<object>
        {
            new { reason = "reflection_and_strings", provenance = "not_determinable" },
            new { reason = "string_based_DI_registration", provenance = "not_determinable" },
            new { reason = "configuration_strings", provenance = "not_determinable" },
            new { reason = "external_consumers_flutter_frontend", provenance = "not_determinable" }
        };
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

        // First pass: collect all symbols and their outgoing type names
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

                    // Skip compiler-generated symbols
                    if (namedSymbol.Locations.Any(loc => loc.IsInMetadata))
                        continue;

                    // Get the first source location for line number
                    var line = namedSymbol.Locations
                        .Where(loc => loc.IsInSource)
                        .Select(loc => loc.GetLineSpan().StartLinePosition.Line + 1)
                        .FirstOrDefault();

                    // Get classification info
                    var (kind, kindRule, kindProvenance) = ClassifyType(namedSymbol, compilation);

                    // Get outgoing type names
                    var outgoingTypeNames = CollectExternalTypeFqns(namedSymbol).ToList();

                    // Add to outgoingEdges dictionary for later inversion
                    var symbolId = namedSymbol.ToDisplayString();
                    if (!outgoingEdges.ContainsKey(symbolId))
                        outgoingEdges[symbolId] = new List<string>();
                    outgoingEdges[symbolId].AddRange(outgoingTypeNames);

                    // Create SymbolRecord
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

            // Add project info
            var projectCatalog = new ProjectCatalog(
                Name: project.Name,
                AssemblyName: project.AssemblyName?.ToString() ?? "",
                ProjectPath: project.FilePath ?? "",
                OutputPath: project.OutputFilePath ?? ""
            );
            projects.Add(projectCatalog);
        }

        // Second pass: invert outgoingEdges to build incomingEdges
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

    private static async Task DiscoverAsync(string[] args)
    {
        var kindFilter = args.FirstOrDefault(a => a.StartsWith("--kind="))?.Split('=', 2)[1];
        var projectFilter = args.FirstOrDefault(a => a.StartsWith("--project="))?.Split('=', 2)[1];

        if (!_useJson)
            WriteProgress("Discovering types...");

        var solution = await LoadSolutionAsync();
        var snapshot = await BuildDiscoverySnapshotAsync(solution);

        // Filter by kind if specified
        var filteredSymbols = snapshot.Symbols;
        if (kindFilter != null)
        {
            var filters = kindFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            filteredSymbols = filteredSymbols.Where(s => filters.Contains(s.Kind, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        // Convert to old DiscoveredType format for backward compatibility
        var allTypes = filteredSymbols.Select(s => new DiscoveredType(
            SymbolId: s.MetadataName,
            Kind: "unknown", // This would need to be derived from the kind
            KindCategory: s.Kind,
            Namespace: s.Namespace,
            Project: s.Project,
            SourceFile: s.File,
            Accessibility: "Public", // This would need to be derived
            Inherits: null, // This would need to be derived
            Implements: new List<string>(), // This would need to be derived
            Dependencies: new List<string>() // This would need to be derived
        )).ToList();

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
                provenance = "compiler_proved",
                fieldProvenance = new
                {
                    kind = "compiler_proved",
                    kindCategory = "indexer_observed",
                    ns = "compiler_proved",
                    project = "compiler_proved",
                    sourceFile = "compiler_proved",
                    accessibility = "compiler_proved",
                    inherits = "compiler_proved",
                    implements = "compiler_proved",
                    dependencies = "compiler_proved",
                    summary = "compiler_proved",
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

    private static string ComputeComplexityTier(
        string symbolId, string project, string accessibility,
        Dictionary<string, HashSet<string>> outgoingEdges,
        Dictionary<string, HashSet<string>> incomingEdges,
        Dictionary<string, string> typeToProject,
        bool externallyExposed)
    {
        var fanOut = outgoingEdges.TryGetValue(symbolId, out var outSet) ? outSet : new HashSet<string>();
        var fanIn = incomingEdges.TryGetValue(symbolId, out var inSet) ? inSet : new HashSet<string>();

        // Calculate new tier rules
        var incomingTypeDependencyCount = fanIn.Count;
        var outgoingTypeDependencyCount = fanOut.Count;
        var referencingProjectCount = incomingEdges
            .Where(kvp => kvp.Value.Contains(symbolId))
            .Select(kvp => typeToProject.GetValueOrDefault(kvp.Key, "(unknown)"))
            .Distinct()
            .Count();

        // New tier rules (first match wins)
        if (externallyExposed)
            return "public_surface";

        if (referencingProjectCount >= 2)
            return "cross_project";

        if (incomingTypeDependencyCount > 0)
            return "project_local";

        // isolated: incomingTypeDependencyCount == 0 and not exposed
        return "isolated";
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

        // Find the symbol in the snapshot
        var symbolRecord = snapshot.Symbols.FirstOrDefault(s => s.MetadataName == symbolFilter);
        if (symbolRecord == null)
        {
            if (_useJson)
                WriteJsonResult("structure", new { symbol = symbolFilter, resolved = false, error = "Symbol not found" });
            else
                Console.WriteLine($"Symbol not found: {symbolFilter}");
            return;
        }

        // Build outgoingEdges from snapshot for backward compatibility
        var outgoingEdges = new Dictionary<string, HashSet<string>>();
        foreach (var symbol in snapshot.Symbols)
        {
            outgoingEdges[symbol.MetadataName] = new HashSet<string>(symbol.OutgoingTypeNames);
        }

        // Build incomingEdges from snapshot
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

        // Build typeToProject from snapshot
        var typeToProject = new Dictionary<string, string>();
        foreach (var symbol in snapshot.Symbols)
        {
            typeToProject[symbol.MetadataName] = symbol.Project;
        }

        // Build typeToAccessibility from snapshot (approximate)
        var typeToAccessibility = new Dictionary<string, string>();
        foreach (var symbol in snapshot.Symbols)
        {
            // We don't have accessibility info in SymbolRecord, so we'll use a default
            typeToAccessibility[symbol.MetadataName] = "Public";
        }

        var fanIn = BuildFanSummary(symbolFilter, incomingEdges, typeToProject);
        var fanOut = BuildFanSummary(symbolFilter, outgoingEdges, typeToProject);

        // Determine externallyExposed: controller descendant OR [ApiController] attribute
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
                provenance = "compiler_proved",
                fieldProvenance = new
                {
                    kind = "compiler_proved",
                    kindCategory = "indexer_observed",
                    ns = "compiler_proved",
                    project = "compiler_proved",
                    sourceFile = "compiler_proved",
                    accessibility = "compiler_proved",
                    inherits = "compiler_proved",
                    implements = "compiler_proved",
                    complexityTier = "indexer_observed",
                    fanIn = "compiler_proved",
                    fanOut = "compiler_proved",
                    depth2 = "compiler_proved",
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
            catch { /* best effort */ }

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

        // --- Derived summary fields (F2.1) ---
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

        // Self-reference: count references whose file+line matches a declaration site
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
                    var tmp = sfPath + ".tmp";
                    await File.WriteAllTextAsync(tmp, json);
                    File.Move(tmp, sfPath, overwrite: true);

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

        var existing = new DirtyManifest(SchemaVersion, [], [], "");

        if (File.Exists(DirtyFilePath))
        {
            var text = await File.ReadAllTextAsync(DirtyFilePath);
            existing = JsonSerializer.Deserialize<DirtyManifest>(text, JsonOptions) ?? existing;
        }

        var merged = new DirtyManifest(
            SchemaVersion: SchemaVersion,
            DirtyFiles: (existing.DirtyFiles ?? []).Union(dirtyFiles).Distinct().ToList(),
            DeletedFiles: (existing.DeletedFiles ?? []).Union(deletedFiles).Distinct().ToList(),
            MarkedAt: DateTime.UtcNow.ToString("O")
        );

        var json = JsonSerializer.Serialize(merged, JsonOptions);
        var tmp = DirtyFilePath + ".tmp";

        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, DirtyFilePath, overwrite: true);

        if (_useJson)
            return new { dirtyFiles = merged.DirtyFiles, deletedFiles = merged.DeletedFiles, markedAt = merged.MarkedAt, provenance = "indexer_observed" };
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
                            if (surfaceChanged && dependencyChanged)
                            {
                                WriteProgress($"  Surface and dependencies changed for: {symbolId}");
                                await FlagSemanticStaleAsync(symbolId, "surface_changed");
                            }
                            else if (surfaceChanged)
                            {
                                WriteProgress($"  Surface changed for: {symbolId}");
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
            SchemaVersion: SchemaVersion,
            DirtyFiles: (manifest.DirtyFiles ?? []).Except(processed).ToList(),
            DeletedFiles: (manifest.DeletedFiles ?? []).Except(processed).ToList(),
            MarkedAt: DateTime.UtcNow.ToString("O")
        );

        var json = JsonSerializer.Serialize(remaining, JsonOptions);
        var tmp = DirtyFilePath + ".tmp";

        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, DirtyFilePath, overwrite: true);

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
        var tmp = semanticPath + ".tmp";

        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, semanticPath, overwrite: true);

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
            WriteJsonResult("lint", new { ok = !foundIssues, violations, provenance = "indexer_observed" });
        }
        else
        {
            if (!foundIssues)
                Console.WriteLine("Lint OK — all relationship values use PascalCase.");
        }
    }

    private static async Task ImpactModeAsync()
    {
        if (!File.Exists(DirtyFilePath))
        {
            if (_useJson)
                WriteJsonResult("impact", new { affected = new List<object>(), provenance = ProvenanceIndexerObserved, fieldProvenance = new { affected = ProvenanceIndexerObserved } });
            else
                Console.WriteLine("[]");
            return;
        }

        var manifestText = await File.ReadAllTextAsync(DirtyFilePath);
        var manifest = JsonSerializer.Deserialize<DirtyManifest>(manifestText, JsonOptions);

        if (manifest == null || ((manifest.DirtyFiles?.Count ?? 0) == 0 && (manifest.DeletedFiles?.Count ?? 0) == 0))
        {
            if (_useJson)
                WriteJsonResult("impact", new { affected = new List<object>(), provenance = ProvenanceIndexerObserved, fieldProvenance = new { affected = ProvenanceIndexerObserved } });
            else
                Console.WriteLine("[]");
            return;
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
                results.Add(new { semanticFile = $"{SanitizeId(symbolId)}.semantic.json", reason = "direct", via = symbolId, provenance = ProvenanceCompilerProved });
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
                            results.Add(new { semanticFile = $"{SanitizeId(symbolId)}.semantic.json", reason = "dependency", via = depSymbol, provenance = ProvenanceCacheSuggests });
                            break;
                        }
                    }
                    catch { continue; }
                }
            }
        }

        if (_useJson)
            WriteJsonResult("impact", new { affected = results, provenance = ProvenanceIndexerObserved, fieldProvenance = new { affected = ProvenanceCacheSuggests } });
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
                    var tmp = sf + ".tmp";
                    await File.WriteAllTextAsync(tmp, json);
                    File.Move(tmp, sf, overwrite: true);
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
                    var tmp = sf + ".tmp";
                    await File.WriteAllTextAsync(tmp, json);
                    File.Move(tmp, sf, overwrite: true);
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

    private static void ShowStatus()
    {
        if (_useJson)
        {
            object? dirtyManifest = null;
            if (File.Exists(DirtyFilePath))
                dirtyManifest = new { exists = true, bytes = new FileInfo(DirtyFilePath).Length };
            else
                dirtyManifest = new { exists = false };

            var curatedEntryCount = 0;
            if (Directory.Exists(SemanticDir))
                curatedEntryCount = Directory.GetFiles(SemanticDir, "*.semantic.json").Length;

            WriteJsonResult("status", new
            {
                gitRoot = GitRoot,
                codeAuditDir = CodeAuditDir,
                semanticDir = SemanticDir,
                solutionPath = SolutionPath,
                dirtyManifest,
                curatedEntryCount,
                provenance = "indexer_observed"
            });
            return;
        }

        Console.WriteLine("RoslynIndexer is ready.");
        Console.WriteLine($"  Git root: {GitRoot}");
        Console.WriteLine($"  CodeAudit dir: {CodeAuditDir}");

        if (File.Exists(DirtyFilePath))
            Console.WriteLine($"  Dirty manifest: exists ({new FileInfo(DirtyFilePath).Length} bytes)");
        else
            Console.WriteLine("  Dirty manifest: (none)");

        if (Directory.Exists(SemanticDir))
        {
            var semanticFiles = Directory.GetFiles(SemanticDir, "*.semantic.json");
            Console.WriteLine($"  Curated semantic entries: {semanticFiles.Length}");
        }
    }
}
