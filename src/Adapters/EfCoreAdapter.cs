using Lurp.Workspace;
﻿using Microsoft.CodeAnalysis;
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

        ExtractDbContextMappings(compilation, allTypes, assemblyIdentity, snapshotId, edges, seen);
        ExtractEntityTypeConfigurations(allTypes, assemblyIdentity, snapshotId, edges, seen);

        return edges;
    }

    private static void ExtractDbContextMappings(Compilation compilation, List<INamedTypeSymbol> allTypes, string assemblyIdentity, string snapshotId,
        List<EdgeRecord> edges, HashSet<(string source, string target, string kind)> seen)
    {
        foreach (var type in allTypes)
        {
            if (!IsDbContext(type))
                continue;

            var dbContextId = MakeSymbolId(type, assemblyIdentity);
            if (dbContextId == null)
                continue;

            ExtractDbSetProperties(type, dbContextId, assemblyIdentity, snapshotId, edges, seen);
            ExtractOnModelCreatingCalls(compilation, type, dbContextId, assemblyIdentity, snapshotId, edges, seen);
        }
    }

    private static void ExtractDbSetProperties(INamedTypeSymbol type, string dbContextId, string assemblyIdentity, string snapshotId,
        List<EdgeRecord> edges, HashSet<(string source, string target, string kind)> seen)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is not IPropertySymbol prop || !IsDbSetType(prop.Type, out var entityType) || entityType == null)
                continue;

            var entityTypeId = MakeSymbolId(entityType, assemblyIdentity);
            if (entityTypeId == null)
                continue;

            var propId = MakeSymbolId(prop, assemblyIdentity);
            var sourceId = propId ?? dbContextId;

            AddMapsToEdge(edges, seen, sourceId, entityTypeId, snapshotId);
        }
    }

    private static void ExtractOnModelCreatingCalls(Compilation compilation, INamedTypeSymbol type, string dbContextId, string assemblyIdentity, string snapshotId,
        List<EdgeRecord> edges, HashSet<(string source, string target, string kind)> seen)
    {
        var onModelCreating = type.GetMembers()
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.Name == "OnModelCreating");

        if (onModelCreating == null)
            return;

        foreach (var syntaxRef in onModelCreating.DeclaringSyntaxReferences)
        {
            if (syntaxRef.GetSyntax() is MethodDeclarationSyntax methodSyntax)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                ExtractEntityCalls(methodSyntax, semanticModel, dbContextId, assemblyIdentity,snapshotId, edges, seen);
            }
        }
    }

    private static void ExtractEntityTypeConfigurations(List<INamedTypeSymbol> allTypes, string assemblyIdentity, string snapshotId,
        List<EdgeRecord> edges, HashSet<(string source, string target, string kind)> seen)
    {
        foreach (var type in allTypes)
        {
            foreach (var iface in type.AllInterfaces)
            {
                if (iface.OriginalDefinition?.Name != "IEntityTypeConfiguration")
                    continue;

                var configId = MakeSymbolId(type, assemblyIdentity);
                if (configId == null)
                    continue;

                var entityTypeArg = iface.TypeArguments.FirstOrDefault();
                if (entityTypeArg is not INamedTypeSymbol entityType)
                    continue;

                var entityTypeId = MakeSymbolId(entityType, assemblyIdentity);
                if (entityTypeId == null)
                    continue;

                AddMapsToEdge(edges, seen, configId, entityTypeId, snapshotId);
            }
        }
    }

    private static void AddMapsToEdge(List<EdgeRecord> edges, HashSet<(string source, string target, string kind)> seen, string sourceId, string targetId, string snapshotId)
    {
        var key = (sourceId, targetId, EdgeKind.MapsTo.ToString());
        if (seen.Add(key))
        {
            edges.Add(new EdgeRecord
            {
                SourceSymbolId = sourceId,
                TargetSymbolId = targetId,
                Kind = EdgeKind.MapsTo.ToString(),
                Provenance = Provenance.FrameworkDerived,
                SnapshotId = snapshotId,
                ExtractorVersion = "efcore-v1",
            });
        }
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

        if (originalDef.Name != "DbSet")
            return false;

        if (namedType.TypeArguments.Length == 1 &&namedType.TypeArguments[0] is INamedTypeSymbol entity)
        {
            entityType = entity;
            return true;
        }

        return false;
    }

    private static void ExtractEntityCalls(MethodDeclarationSyntax methodSyntax,SemanticModel semanticModel,string dbContextId,string assemblyIdentity,string snapshotId,List<EdgeRecord> edges,HashSet<(string source, string target, string kind)> seen)
    {
        foreach (var invocation in methodSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            if (memberAccess.Name is GenericNameSyntax genericName && genericName.Identifier.Text == "Entity")
            {
                ExtractEntityMethodMapping(genericName, semanticModel, dbContextId, assemblyIdentity, snapshotId, edges, seen);
            }

            if (memberAccess.Name.Identifier.Text is "HasOne" or "HasMany" or "WithOne" or "WithMany")
            {
                ExtractNavigationTypeReference(invocation, semanticModel, dbContextId, assemblyIdentity, snapshotId, edges, seen);
            }
        }
    }

    private static void ExtractEntityMethodMapping(GenericNameSyntax genericName, SemanticModel semanticModel, string dbContextId, string assemblyIdentity, string snapshotId,
        List<EdgeRecord> edges, HashSet<(string source, string target, string kind)> seen)
    {
        if (genericName.TypeArgumentList.Arguments.Count != 1)
            return;

        var typeInfo = semanticModel.GetTypeInfo(genericName.TypeArgumentList.Arguments[0]);
        if (typeInfo.Type is not INamedTypeSymbol entityType)
            return;

        var entityTypeId = MakeSymbolId(entityType, assemblyIdentity);
        if (entityTypeId == null)
            return;

        AddMapsToEdge(edges, seen, dbContextId, entityTypeId, snapshotId);
    }

    private static void ExtractNavigationTypeReference(InvocationExpressionSyntax invocation, SemanticModel semanticModel, string dbContextId, string assemblyIdentity, string snapshotId,
        List<EdgeRecord> edges, HashSet<(string source, string target, string kind)> seen)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
        if (symbolInfo.Symbol is not IMethodSymbol navMethod || navMethod.TypeArguments.Length == 0)
            return;

        if (navMethod.TypeArguments[0] is not INamedTypeSymbol navNamedType)
            return;

        var navTypeId = MakeSymbolId(navNamedType, assemblyIdentity);
        if (navTypeId == null)
            return;

        var key = (dbContextId, navTypeId, EdgeKind.References.ToString());
        if (seen.Add(key))
        {
            edges.Add(new EdgeRecord
            {
                SourceSymbolId = dbContextId,
                TargetSymbolId = navTypeId,
                Kind = EdgeKind.References.ToString(),
                Provenance = Provenance.FrameworkDerived,
                SnapshotId = snapshotId,
                ExtractorVersion = "efcore-v1",
            });
        }
    }

    private static string? MakeSymbolId(ISymbol symbol, string assemblyIdentity)
    {
        return SymbolIdFactory.Make(symbol, assemblyIdentity);
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
