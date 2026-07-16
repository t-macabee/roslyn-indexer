using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class EfCoreAdapter : IFrameworkAdapter
{
    public string Name => "EF Core";
    public string Version => "efcore-v1";

    public List<EdgeRecord> Extract(Compilation compilation, string snapshotId)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();
        var assemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
        var allTypes = GetAllNamedTypes(compilation.Assembly.GlobalNamespace);

        foreach (var type in allTypes)
        {
            if (!IsDbContext(type))
                continue;

            var dbContextId = MakeSymbolId(type, assemblyIdentity);
            if (dbContextId == null)
                continue;

            
            foreach (var member in type.GetMembers())
            {
                if (member is IPropertySymbol prop &&
                    IsDbSetType(prop.Type, out var entityType))
                {
                    if (entityType == null) continue;
                    var entityTypeId = MakeSymbolId(entityType, assemblyIdentity);
                    if (entityTypeId == null)
                        continue;

                    var propId = MakeSymbolId(prop, assemblyIdentity);
                    var sourceId = propId ?? dbContextId;

                    var key = (sourceId, entityTypeId, EdgeKind.MapsTo.ToString());
                    if (seen.Add(key))
                    {
                        edges.Add(new EdgeRecord(
                            sourceSymbolId: sourceId,
                            targetSymbolId: entityTypeId,
                            kind: EdgeKind.MapsTo.ToString(),
                            provenance: "framework_derived",
                            snapshotId: snapshotId,
                            extractorVersion: Version));
                    }
                }
            }

            
            var onModelCreating = type.GetMembers()
                .OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.Name == "OnModelCreating");

            if (onModelCreating != null)
            {
                foreach (var syntaxRef in onModelCreating.DeclaringSyntaxReferences)
                {
                    if (syntaxRef.GetSyntax() is MethodDeclarationSyntax methodSyntax)
                    {
                        var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                        ExtractEntityCalls(methodSyntax, semanticModel, dbContextId, assemblyIdentity,
                            snapshotId, edges, seen);
                    }
                }
            }
        }

        
        foreach (var type in allTypes)
        {
            foreach (var iface in type.AllInterfaces)
            {
                if (iface.OriginalDefinition?.Name == "IEntityTypeConfiguration`1")
                {
                    var configId = MakeSymbolId(type, assemblyIdentity);
                    if (configId == null)
                        continue;

                    var entityTypeArg = iface.TypeArguments.FirstOrDefault();
                    if (entityTypeArg is INamedTypeSymbol entityType)
                    {
                        var entityTypeId = MakeSymbolId(entityType, assemblyIdentity);
                        if (entityTypeId != null)
                        {
                            var key = (configId, entityTypeId, EdgeKind.MapsTo.ToString());
                            if (seen.Add(key))
                            {
                                edges.Add(new EdgeRecord(
                                    sourceSymbolId: configId,
                                    targetSymbolId: entityTypeId,
                                    kind: EdgeKind.MapsTo.ToString(),
                                    provenance: "framework_derived",
                                    snapshotId: snapshotId,
                                    extractorVersion: Version));
                            }
                        }
                    }
                }
            }
        }

        return edges;
    }

    private static bool IsDbContext(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class)
            return false;

        var current = type.BaseType;
        while (current != null)
        {
            if (current.Name == "DbContext")
                return true;
            current = current.BaseType;
        }

        return false;
    }

    private static bool IsDbSetType(ITypeSymbol type, out INamedTypeSymbol? entityType)
    {
        entityType = null;

        if (type is not INamedTypeSymbol namedType)
            return false;

        var originalDef = namedType.OriginalDefinition;
        if (originalDef == null)
            return false;

        if (originalDef.Name != "DbSet`1")
            return false;

        if (namedType.TypeArguments.Length == 1 &&
            namedType.TypeArguments[0] is INamedTypeSymbol entity)
        {
            entityType = entity;
            return true;
        }

        return false;
    }

    private static void ExtractEntityCalls(
        MethodDeclarationSyntax methodSyntax,
        SemanticModel semanticModel,
        string dbContextId,
        string assemblyIdentity,
        string snapshotId,
        List<EdgeRecord> edges,
        HashSet<(string source, string target, string kind)> seen)
    {
        
        var invocations = methodSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name is GenericNameSyntax genericName &&
                genericName.Identifier.Text == "Entity")
            {
                
                if (genericName.TypeArgumentList.Arguments.Count == 1)
                {
                    var typeInfo = semanticModel.GetTypeInfo(genericName.TypeArgumentList.Arguments[0]);
                    if (typeInfo.Type is INamedTypeSymbol entityType)
                    {
                        var entityTypeId = MakeSymbolId(entityType, assemblyIdentity);
                        if (entityTypeId != null)
                        {
                            var key = (dbContextId, entityTypeId, EdgeKind.MapsTo.ToString());
                            if (seen.Add(key))
                            {
                                edges.Add(new EdgeRecord(
                                    sourceSymbolId: dbContextId,
                                    targetSymbolId: entityTypeId,
                                    kind: EdgeKind.MapsTo.ToString(),
                                    provenance: "framework_derived",
                                    snapshotId: snapshotId,
                                    extractorVersion: "efcore-v1"));
                            }
                        }
                    }
                }
            }

            
            if (invocation.Expression is MemberAccessExpressionSyntax navAccess)
            {
                var methodName = navAccess.Name.Identifier.Text;
                if (methodName is "HasOne" or "HasMany" or "WithOne" or "WithMany")
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                    if (symbolInfo.Symbol is IMethodSymbol navMethod)
                    {
                        
                        if (navMethod.TypeArguments.Length > 0)
                        {
                            var navType = navMethod.TypeArguments[0];
                            if (navType is INamedTypeSymbol navNamedType)
                            {
                                var navTypeId = MakeSymbolId(navNamedType, assemblyIdentity);
                                if (navTypeId != null)
                                {
                                    var key = (dbContextId, navTypeId, EdgeKind.References.ToString());
                                    if (seen.Add(key))
                                    {
                                        edges.Add(new EdgeRecord(
                                            sourceSymbolId: dbContextId,
                                            targetSymbolId: navTypeId,
                                            kind: EdgeKind.References.ToString(),
                                            provenance: "framework_derived",
                                            snapshotId: snapshotId,
                                            extractorVersion: "efcore-v1"));
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private static string? MakeSymbolId(ISymbol symbol, string assemblyIdentity)
    {
        var docCommentId = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(docCommentId))
            return null;
        return $"{docCommentId}|{assemblyIdentity}";
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
