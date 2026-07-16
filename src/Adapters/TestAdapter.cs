using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class TestAdapter : IFrameworkAdapter
{
    public string Name => "Test";
    public string Version => "test-v1";

    public List<EdgeRecord> Extract(Compilation compilation, string snapshotId)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();
        var assemblyIdentity = compilation.Assembly.Identity.GetDisplayName();

        
        bool isTestProject = IsTestProject(compilation);
        if (!isTestProject)
            return edges;

        var semanticModelCache = new Dictionary<SyntaxTree, SemanticModel>();

        
        foreach (var type in GetAllNamedTypes(compilation.Assembly.GlobalNamespace))
        {
            foreach (var member in type.GetMembers())
            {
                if (member is not IMethodSymbol method)
                    continue;

                if (!IsTestMethod(method))
                    continue;

                var testMethodId = MakeSymbolId(method, assemblyIdentity);
                if (testMethodId == null)
                    continue;

                
                foreach (var syntaxRef in method.DeclaringSyntaxReferences)
                {
                    if (syntaxRef.GetSyntax() is not MethodDeclarationSyntax methodSyntax)
                        continue;

                    var bodySyntax = methodSyntax.Body ?? (SyntaxNode?)methodSyntax.ExpressionBody;
                    if (bodySyntax == null)
                        continue;

                    var semanticModel = GetOrCreateSemanticModel(methodSyntax.SyntaxTree, semanticModelCache, compilation);

                    
                    var referencedSymbols = new HashSet<string>();

                    
                    foreach (var invocation in bodySyntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                        if (symbolInfo.Symbol != null)
                            TryAddProductionRef(symbolInfo.Symbol, assemblyIdentity, seen, edges,
                                testMethodId, snapshotId, referencedSymbols);
                    }

                    
                    foreach (var creation in bodySyntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
                    {
                        var symbolInfo = semanticModel.GetSymbolInfo(creation);
                        if (symbolInfo.Symbol != null)
                            TryAddProductionRef(symbolInfo.Symbol, assemblyIdentity, seen, edges,
                                testMethodId, snapshotId, referencedSymbols);
                    }

                    
                    foreach (var memberAccess in bodySyntax.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                    {
                        var symbolInfo = semanticModel.GetSymbolInfo(memberAccess);
                        if (symbolInfo.Symbol != null)
                            TryAddProductionRef(symbolInfo.Symbol, assemblyIdentity, seen, edges,
                                testMethodId, snapshotId, referencedSymbols);
                    }
                }
            }
        }

        return edges;
    }

    private static bool IsTestProject(Compilation compilation)
    {
        
        var assemblyName = compilation.Assembly.Name;
        if (assemblyName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
            assemblyName.EndsWith(".Test", StringComparison.OrdinalIgnoreCase) ||
            assemblyName.EndsWith(".Specs", StringComparison.OrdinalIgnoreCase) ||
            assemblyName.EndsWith(".IntegrationTests", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        
        foreach (var refAsm in compilation.ReferencedAssemblyNames)
        {
            var name = refAsm.Name;
            if (name.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("nunit", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("MSTest", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Microsoft.VisualStudio.TestPlatform", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTestMethod(IMethodSymbol method)
    {
        foreach (var attr in method.GetAttributes())
        {
            if (attr.AttributeClass == null)
                continue;

            var name = attr.AttributeClass.Name;
            if (name is "Fact" or "Theory" or "Test" or "TestMethod")
                return true;

            
            var fullName = attr.AttributeClass.ToDisplayString();
            if (fullName is "Xunit.FactAttribute" or "Xunit.TheoryAttribute" or
                "NUnit.Framework.TestAttribute" or
                "Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute")
                return true;
        }

        return false;
    }

    private static void TryAddProductionRef(
        ISymbol symbol,
        string assemblyIdentity,
        HashSet<(string source, string target, string kind)> seen,
        List<EdgeRecord> edges,
        string testMethodId,
        string snapshotId,
        HashSet<string> referencedSymbols)
    {
        
        var productionSymbol = symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol
            ? symbol.ContainingType
            : symbol;

        if (productionSymbol == null)
            return;

        
        var productionAssembly = productionSymbol.ContainingAssembly;
        if (productionAssembly != null && productionAssembly.Identity.GetDisplayName() == assemblyIdentity)
            return;

        var productionId = MakeSymbolId(productionSymbol, assemblyIdentity);
        if (productionId == null)
            return;

        
        if (!referencedSymbols.Add(productionId))
            return;

        var key = (productionId, testMethodId, EdgeKind.TestedBy.ToString());
        if (seen.Add(key))
        {
            edges.Add(new EdgeRecord(
                sourceSymbolId: productionId,
                targetSymbolId: testMethodId,
                kind: EdgeKind.TestedBy.ToString(),
                provenance: "framework_derived",
                snapshotId: snapshotId,
                extractorVersion: "test-v1"));
        }
    }

    private static string? MakeSymbolId(ISymbol symbol, string assemblyIdentity)
    {
        var docCommentId = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(docCommentId))
            return null;
        return $"{docCommentId}|{assemblyIdentity}";
    }

    private static SemanticModel GetOrCreateSemanticModel(
        SyntaxTree syntaxTree,
        Dictionary<SyntaxTree, SemanticModel> cache,
        Compilation compilation)
    {
        if (!cache.TryGetValue(syntaxTree, out var model))
        {
            model = compilation.GetSemanticModel(syntaxTree);
            cache[syntaxTree] = model;
        }
        return model;
    }

    private static List<INamedTypeSymbol> GetAllNamedTypes(INamespaceSymbol ns)
    {
        var types = new List<INamedTypeSymbol>();
        CollectTypes(ns, types);
        return types;
    }

    private static void CollectTypes(INamespaceSymbol ns, List<INamedTypeSymbol> types)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            types.Add(type);
            foreach (var nested in type.GetTypeMembers())
                types.Add(nested);
        }

        foreach (var childNs in ns.GetNamespaceMembers())
            CollectTypes(childNs, types);
    }
}
