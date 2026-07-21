using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Lurp.Storage;
using Lurp.Workspace;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class DependencyInjectionAdapter : IFrameworkAdapter
{
    public string Name => "Dependency Injection";
    public string Version => "di-v1";

    private sealed record ExtractionContext(
        string AssemblyIdentity,
        string SnapshotId,
        string ExtractorVersion,
        List<EdgeRecord> Edges,
        HashSet<(string source, string target, string kind)> Seen
    );

    private static readonly HashSet<string> _conventionMethodNames =
    [
        "Scan", "AddClasses", "AsImplementedInterfaces",
        "AsMatchingInterface", "UsingRegistrationStrategy", "AddAssemblyTypes",
    ];

    public List<EdgeRecord> Extract(Compilation compilation, string snapshotId)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        var ctx = new ExtractionContext(
            AssemblyIdentity: compilation.Assembly.Identity.GetDisplayName(),
            SnapshotId: snapshotId,
            ExtractorVersion: ExtractorConstants.DependencyInjectionExtractor,
            Edges: edges,
            Seen: seen
        );

        var serviceCollectionType = compilation.GetTypeByMetadataName("Microsoft.Extensions.DependencyInjection.IServiceCollection");

        foreach (var tree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);

            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
                    continue;

                var methodName = methodSymbol.Name;

                if (methodName is "AddScoped" or "AddTransient" or "AddSingleton")
                {
                    ProcessExplicitGeneric(invocation, methodSymbol, semanticModel, ctx);
                    continue;
                }

                if (_conventionMethodNames.Contains(methodName))
                {
                    ProcessConventionCandidate(invocation, methodSymbol, semanticModel, compilation, ctx);
                    continue;
                }

                if (methodName is "AddHostedService" or "Configure" or "AddOptions")
                {
                    ProcessRuntimeUnknown(invocation, semanticModel, ctx);
                    continue;
                }

                if (serviceCollectionType != null && IsExternalMethodWithServiceCollectionParam(methodSymbol, compilation, serviceCollectionType))
                {
                    ProcessRuntimeUnknown(invocation, semanticModel, ctx);
                }
            }
        }

        return edges;
    }

    private static void ProcessExplicitGeneric(InvocationExpressionSyntax invocation, IMethodSymbol methodSymbol, SemanticModel semanticModel, ExtractionContext ctx)
    {
        if (!IsDependencyInjectionExtensionMethod(methodSymbol))
            return;

        var sourceId = ResolveSourceId(invocation, semanticModel, ctx.AssemblyIdentity);
        if (sourceId == null)
            return;

        var typeArgs = ResolveRegistrationTypeArgs(invocation, semanticModel);
        if (typeArgs.Count == 0)
            return;

        var implTypeId = MakeSymbolId(typeArgs[^1], ctx.AssemblyIdentity);
        if (implTypeId == null)
            return;

        var key = (sourceId, implTypeId, EdgeKind.Registers.ToString());
        if (ctx.Seen.Add(key))
        {
            ctx.Edges.Add(new EdgeRecord
            {
                SourceSymbolId = sourceId,
                TargetSymbolId = implTypeId,
                Kind = EdgeKind.Registers.ToString(),
                Provenance = Provenance.FrameworkDerived,
                SnapshotId = ctx.SnapshotId,
                ExtractorVersion = ctx.ExtractorVersion,
            });
        }
    }

    private static bool IsDependencyInjectionExtensionMethod(IMethodSymbol methodSymbol)
    {
        var current = methodSymbol.ContainingType;
        while (current != null)
        {
            if (current.Name is "ServiceCollectionServiceExtensions" or "ExtensionsServiceCollectionExtensions" or "ServiceCollectionDescriptorExtensions")
                return true;
            current = current.BaseType;
        }
        return false;
    }

    private static List<ITypeSymbol> ResolveRegistrationTypeArgs(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        var typeArgs = new List<ITypeSymbol>();

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess && memberAccess.Name is GenericNameSyntax genericName)
        {
            foreach (var typeArg in genericName.TypeArgumentList.Arguments)
            {
                var typeInfo = semanticModel.GetTypeInfo(typeArg);
                if (typeInfo.Type != null)
                    typeArgs.Add(typeInfo.Type);
            }
        }

        if (typeArgs.Count == 0)
        {
            foreach (var arg in invocation.ArgumentList.Arguments)
            {
                if (arg.Expression is TypeOfExpressionSyntax typeofExpr)
                {
                    var typeInfo = semanticModel.GetTypeInfo(typeofExpr.Type);
                    if (typeInfo.Type != null)
                        typeArgs.Add(typeInfo.Type);
                }
            }
        }

        return typeArgs;
    }


    private static void ProcessConventionCandidate(InvocationExpressionSyntax invocation, IMethodSymbol methodSymbol, SemanticModel semanticModel, Compilation compilation, ExtractionContext ctx)
    {
        var sourceId = ResolveSourceId(invocation, semanticModel, ctx.AssemblyIdentity);
        if (sourceId == null)
            return;

        var assemblyName = ExtractConventionAssemblyName(invocation, methodSymbol, semanticModel, compilation, ctx.AssemblyIdentity);

        var targetId = $"convention:assembly_scan:{assemblyName}";

        var key = (sourceId, targetId, EdgeKind.Registers.ToString());
        if (ctx.Seen.Add(key))
        {
            ctx.Edges.Add(new EdgeRecord
            {
                SourceSymbolId = sourceId,
                TargetSymbolId = targetId,
                Kind = EdgeKind.Registers.ToString(),
                Provenance = Provenance.Convention,
                SnapshotId = ctx.SnapshotId,
                ExtractorVersion = ctx.ExtractorVersion,
            });
        }
    }

    
    private static string ExtractConventionAssemblyName(InvocationExpressionSyntax invocation,IMethodSymbol methodSymbol,SemanticModel semanticModel,Compilation compilation,string fallback)
    {
        var directAssembly = ResolveAssemblyFromGenericTypeArgs(invocation, semanticModel);
        if (directAssembly != null)
            return directAssembly;

        if (methodSymbol.Name == "Scan")
        {
            var scannedAssembly = ResolveAssemblyFromScanLambda(invocation, semanticModel);
            if (scannedAssembly != null)
                return scannedAssembly;
        }

        return fallback;
    }

    private static string? ResolveAssemblyFromGenericTypeArgs(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess || memberAccess.Name is not GenericNameSyntax genericName)
            return null;

        foreach (var typeArg in genericName.TypeArgumentList.Arguments)
        {
            var typeInfo = semanticModel.GetTypeInfo(typeArg);
            if (typeInfo.Type?.ContainingAssembly != null)
                return typeInfo.Type.ContainingAssembly.Identity.GetDisplayName();
        }

        return null;
    }

    private static string? ResolveAssemblyFromScanLambda(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            if (arg.Expression is not LambdaExpressionSyntax lambda)
                continue;

            foreach (var nested in lambda.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var assembly = ResolveAssemblyFromAssemblyScanCall(nested, semanticModel);
                if (assembly != null)
                    return assembly;
            }
        }

        return null;
    }

    private static string? ResolveAssemblyFromAssemblyScanCall(InvocationExpressionSyntax nested, SemanticModel semanticModel)
    {
        if (nested.Expression is not MemberAccessExpressionSyntax nestedAccess || nestedAccess.Name is not SimpleNameSyntax nestedName)
            return null;

        if (nestedName.Identifier.Text != "FromAssemblyOf" && nestedName.Identifier.Text != "FromAssembliesOf")
            return null;

        if (nestedAccess.Name is GenericNameSyntax nestedGeneric)
        {
            foreach (var typeArg in nestedGeneric.TypeArgumentList.Arguments)
            {
                var typeInfo = semanticModel.GetTypeInfo(typeArg);
                if (typeInfo.Type?.ContainingAssembly != null)
                    return typeInfo.Type.ContainingAssembly.Identity.GetDisplayName();
            }
        }

        foreach (var nestedArg in nested.ArgumentList.Arguments)
        {
            if (nestedArg.Expression is TypeOfExpressionSyntax typeofExpr)
            {
                var typeInfo = semanticModel.GetTypeInfo(typeofExpr.Type);
                if (typeInfo.Type?.ContainingAssembly != null)
                    return typeInfo.Type.ContainingAssembly.Identity.GetDisplayName();
            }
        }

        return null;
    }

    private static void ProcessRuntimeUnknown(InvocationExpressionSyntax invocation, SemanticModel semanticModel, ExtractionContext ctx)
    {
        var sourceId = ResolveSourceId(invocation, semanticModel, ctx.AssemblyIdentity);

        if (sourceId == null)
            return;

        const string targetId = "runtime:unknown";

        var key = (sourceId, targetId, EdgeKind.Registers.ToString());

        if (ctx.Seen.Add(key))
        {
            ctx.Edges.Add(new EdgeRecord
            {
                SourceSymbolId = sourceId,
                TargetSymbolId = targetId,
                Kind = EdgeKind.Registers.ToString(),
                Provenance = Provenance.RuntimeUnknown,
                SnapshotId = ctx.SnapshotId,
                ExtractorVersion = ctx.ExtractorVersion,
            });
        }
    }

    /// <summary>
    /// Returns true when <paramref name="methodSymbol"/> is defined outside
    /// the current <paramref name="compilation"/> and has at least one
    /// parameter whose type is <paramref name="serviceCollectionType"/>.
    /// </summary>
    private static bool IsExternalMethodWithServiceCollectionParam(IMethodSymbol methodSymbol,Compilation compilation,INamedTypeSymbol serviceCollectionType)
    {
        if (SymbolEqualityComparer.Default.Equals(methodSymbol.ContainingAssembly, compilation.Assembly))
            return false;

        foreach (var param in methodSymbol.Parameters)
        {
            if (SymbolEqualityComparer.Default.Equals(param.Type, serviceCollectionType))
                return true;
        }

        return false;
    }

    // ────────────────────────────────────────────────────────────────
    // Shared helpers
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the source symbol ID for an invocation. Prefers the enclosing
    /// method, falling back to the enclosing type declaration.
    /// </summary>
    private static string? ResolveSourceId(InvocationExpressionSyntax invocation,SemanticModel semanticModel,string assemblyIdentity)
    {
        var containingMethod = invocation.Ancestors()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();

        if (containingMethod != null)
        {
            var methodSym = semanticModel.GetDeclaredSymbol(containingMethod);
            if (methodSym != null)
            {
                var id = MakeSymbolId(methodSym, assemblyIdentity);
                if (id != null)
                    return id;
            }
        }

        var containingTypeDecl = invocation.Ancestors()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();

        if (containingTypeDecl != null)
        {
            var typeSym = semanticModel.GetDeclaredSymbol(containingTypeDecl);
            if (typeSym != null)
            {
                var id = MakeSymbolId(typeSym, assemblyIdentity);
                if (id != null)
                    return id;
            }
        }

        return null;
    }

    private static string? MakeSymbolId(ISymbol symbol, string assemblyIdentity)
    {
        var docCommentId = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(docCommentId))
            return null;
        return $"{docCommentId}|{assemblyIdentity}";
    }
}
