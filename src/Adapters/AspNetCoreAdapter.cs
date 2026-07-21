using Lurp.Workspace;
﻿using Microsoft.CodeAnalysis;
using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class AspNetCoreAdapter : IFrameworkAdapter
{
    public string Name => "ASP.NET Core";
    public string Version => "aspnetcore-v1";

    public List<EdgeRecord> Extract(Compilation compilation, string snapshotId)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();
        var assemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
        var allTypes = GetAllNamedTypes(compilation.Assembly.GlobalNamespace);

        foreach (var type in allTypes)
        {
            if (!IsController(type))
                continue;

            var controllerId = MakeSymbolId(type, assemblyIdentity);
            if (controllerId == null)
                continue;

            foreach (var member in type.GetMembers())
            {
                if (member is not IMethodSymbol method || method.MethodKind != MethodKind.Ordinary)
                    continue;

                ProcessControllerAction(method, controllerId, assemblyIdentity, snapshotId, edges, seen);
            }
        }

        return edges;
    }

    private void ProcessControllerAction(IMethodSymbol method, string controllerId, string assemblyIdentity, string snapshotId,
        List<EdgeRecord> edges, HashSet<(string source, string target, string kind)> seen)
    {
        var methodId = MakeSymbolId(method, assemblyIdentity);
        if (methodId == null)
            return;

        var declaresKey = (controllerId, methodId, EdgeKind.Declares.ToString());
        if (seen.Add(declaresKey))
            edges.Add(MakeEdge(controllerId, methodId, EdgeKind.Declares.ToString(), snapshotId, assemblyIdentity));

        var routeTemplate = ExtractRouteTemplate((INamedTypeSymbol)method.ContainingType, method);
        if (routeTemplate != null)
        {
            var routeSourceId = $"route://{routeTemplate}";
            var routeKey = (routeSourceId, methodId, EdgeKind.RoutesTo.ToString());
            if (seen.Add(routeKey))
                edges.Add(new EdgeRecord
                {
                    SourceSymbolId = routeSourceId,
                    TargetSymbolId = methodId,
                    Kind = EdgeKind.RoutesTo.ToString(),
                    Provenance = Provenance.FrameworkDerived,
                    SnapshotId = snapshotId,
                    ExtractorVersion = Version,
                });
        }

        if (!method.ReturnsVoid && method.ReturnType != null)
        {
            var returnTypeId = MakeSymbolId(method.ReturnType, assemblyIdentity);
            if (returnTypeId != null)
            {
                var retKey = (methodId, returnTypeId, EdgeKind.Returns.ToString());
                if (seen.Add(retKey))
                    edges.Add(MakeEdge(methodId, returnTypeId, EdgeKind.Returns.ToString(), snapshotId, assemblyIdentity));
            }
        }

        foreach (var param in method.Parameters)
        {
            var hasFromServices = param.GetAttributes().Any(a => a.AttributeClass?.Name is "FromServicesAttribute" or "FromServices");
            if (!hasFromServices)
                continue;

            var paramTypeId = MakeSymbolId(param.Type, assemblyIdentity);
            if (paramTypeId == null)
                continue;

            var refKey = (methodId, paramTypeId, EdgeKind.References.ToString());
            if (seen.Add(refKey))
                edges.Add(MakeEdge(methodId, paramTypeId, EdgeKind.References.ToString(), snapshotId, assemblyIdentity));
        }
    }

    private static bool IsController(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class)
            return false;

        var current = type.BaseType;

        while (current != null)
        {
            if (current.Name is "ControllerBase" or "Controller")
                return true;

            current = current.BaseType;
        }

        return false;
    }

    private static string? ExtractRouteTemplate(INamedTypeSymbol controller, IMethodSymbol action)
    {
        var parts = new List<string>();

        var classRoute = controller.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name is "RouteAttribute" or "Route");

        if (classRoute?.ConstructorArguments.Length > 0 && classRoute.ConstructorArguments[0].Value is string classTemplate)
        {
            parts.Add(classTemplate.TrimStart('/'));
        }

        var methodRoute = action.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name is"RouteAttribute" or "Route" or"HttpGetAttribute" or "HttpGet" or"HttpPostAttribute" or "HttpPost" or"HttpPutAttribute" or "HttpPut" or"HttpDeleteAttribute" or "HttpDelete" or"HttpPatchAttribute" or "HttpPatch");

        if (methodRoute?.ConstructorArguments.Length > 0 && methodRoute.ConstructorArguments[0].Value is string methodTemplate)
        {
            if (methodTemplate.StartsWith("/"))
                return methodTemplate.TrimStart('/');

            parts.Add(methodTemplate);
        }

        return parts.Count > 0 ? string.Join("/", parts) : null;
    }

    private static string? MakeSymbolId(ISymbol symbol, string assemblyIdentity)
    {
        var docCommentId = symbol.GetDocumentationCommentId();

        if (string.IsNullOrEmpty(docCommentId))
            return null;

        return $"{docCommentId}|{assemblyIdentity}";
    }

    private static EdgeRecord MakeEdge(string sourceId, string targetId, string kind, string snapshotId, string assemblyIdentity)
    {
        ArgumentNullException.ThrowIfNull(assemblyIdentity);

        return new EdgeRecord
        {
            SourceSymbolId = sourceId,
            TargetSymbolId = targetId,
            Kind = kind,
            Provenance = Provenance.FrameworkDerived,
            SnapshotId = snapshotId,
            ExtractorVersion = "aspnetcore-v1",
        };
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
