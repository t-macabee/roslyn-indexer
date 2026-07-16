using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class DependencyInjectionAdapter : IFrameworkAdapter
{
    public string Name => "Dependency Injection";
    public string Version => "di-v1";

    public List<EdgeRecord> Extract(Compilation compilation, string snapshotId)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();
        var assemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
        var semanticModelCache = new Dictionary<SyntaxTree, SemanticModel>();

        
        foreach (var tree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);

            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
                    continue;

                var methodName = methodSymbol.Name;
                if (methodName is not ("AddScoped" or "AddTransient" or "AddSingleton"))
                    continue;

                
                var containingType = methodSymbol.ContainingType;
                if (containingType == null)
                    continue;

                bool isDiExtension = false;
                var current = containingType;
                while (current != null)
                {
                    if (current.Name is "ServiceCollectionServiceExtensions" or
                        "ExtensionsServiceCollectionExtensions" or
                        "ServiceCollectionDescriptorExtensions")
                    {
                        isDiExtension = true;
                        break;
                    }
                    current = current.BaseType;
                }

                if (!isDiExtension)
                    continue;

                
                var containingMethod = invocation.Ancestors()
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault();

                string? sourceId = null;
                if (containingMethod != null)
                {
                    var methodSym = semanticModel.GetDeclaredSymbol(containingMethod);
                    if (methodSym != null)
                        sourceId = MakeSymbolId(methodSym, assemblyIdentity);
                }

                if (sourceId == null)
                {
                    
                    var containingTypeDecl = invocation.Ancestors()
                        .OfType<TypeDeclarationSyntax>()
                        .FirstOrDefault();
                    if (containingTypeDecl != null)
                    {
                        var typeSym = semanticModel.GetDeclaredSymbol(containingTypeDecl);
                        if (typeSym != null)
                            sourceId = MakeSymbolId(typeSym, assemblyIdentity);
                    }
                }

                if (sourceId == null)
                    continue;

                
                
                var typeArgs = new List<ITypeSymbol>();

                
                if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                    memberAccess.Name is GenericNameSyntax genericName)
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

                
                
                
                if (typeArgs.Count >= 2)
                {
                    
                    var implTypeId = MakeSymbolId(typeArgs[typeArgs.Count - 1], assemblyIdentity);
                    if (implTypeId != null)
                    {
                        var key = (sourceId, implTypeId, EdgeKind.Registers.ToString());
                        if (seen.Add(key))
                        {
                            edges.Add(new EdgeRecord(
                                sourceSymbolId: sourceId,
                                targetSymbolId: implTypeId,
                                kind: EdgeKind.Registers.ToString(),
                                provenance: "framework_derived",
                                snapshotId: snapshotId,
                                extractorVersion: Version));
                        }
                    }
                }
                else if (typeArgs.Count == 1)
                {
                    var implTypeId = MakeSymbolId(typeArgs[0], assemblyIdentity);
                    if (implTypeId != null)
                    {
                        var key = (sourceId, implTypeId, EdgeKind.Registers.ToString());
                        if (seen.Add(key))
                        {
                            edges.Add(new EdgeRecord(
                                sourceSymbolId: sourceId,
                                targetSymbolId: implTypeId,
                                kind: EdgeKind.Registers.ToString(),
                                provenance: "framework_derived",
                                snapshotId: snapshotId,
                                extractorVersion: Version));
                        }
                    }
                }
            }
        }

        return edges;
    }

    private static string? MakeSymbolId(ISymbol symbol, string assemblyIdentity)
    {
        var docCommentId = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(docCommentId))
            return null;
        return $"{docCommentId}|{assemblyIdentity}";
    }
}
