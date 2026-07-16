using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class MediatRAdapter : IFrameworkAdapter
{
    public string Name => "MediatR";
    public string Version => "mediatr-v1";

    public List<EdgeRecord> Extract(Compilation compilation, string snapshotId)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();
        var assemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
        var allTypes = GetAllNamedTypes(compilation.Assembly.GlobalNamespace);

        // Check if MediatR types are referenced at all
        bool hasMediatRReferences = compilation.ReferencedAssemblyNames.Any(a =>
            a.Name.Contains("MediatR", StringComparison.OrdinalIgnoreCase));

        if (!hasMediatRReferences)
            return edges;

        // Find IRequest<T> and IRequest implementations (requests/commands/queries)
        var requestTypes = new List<INamedTypeSymbol>();
        // Find IRequestHandler<TRequest, TResponse> implementations (handlers)
        var handlerTypes = new List<(INamedTypeSymbol HandlerType, INamedTypeSymbol RequestType)>();

        foreach (var type in allTypes)
        {
            if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct)
                continue;

            foreach (var iface in type.AllInterfaces)
            {
                var ifaceName = iface.OriginalDefinition?.Name;

                if (ifaceName == "IRequest`1" || ifaceName == "IRequest")
                {
                    requestTypes.Add(type);
                }

                if (ifaceName == "IRequestHandler`2")
                {
                    var requestTypeArg = iface.TypeArguments.FirstOrDefault();
                    if (requestTypeArg is INamedTypeSymbol namedRequest)
                        handlerTypes.Add((type, namedRequest));
                }

                if (ifaceName == "INotificationHandler`1")
                {
                    // Also handle notifications — treated as handlers
                    var notificationTypeArg = iface.TypeArguments.FirstOrDefault();
                    if (notificationTypeArg is INamedTypeSymbol namedNotification)
                        handlerTypes.Add((type, namedNotification));
                }
            }
        }

        // Emit Handles edges from request -> handler's Handle method
        foreach (var (handlerType, requestType) in handlerTypes)
        {
            var requestId = MakeSymbolId(requestType, assemblyIdentity);
            if (requestId == null)
                continue;

            // Find the Handle method on the handler
            var handleMethod = handlerType.GetMembers()
                .OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.Name == "Handle");

            if (handleMethod == null)
                continue;

            var handleMethodId = MakeSymbolId(handleMethod, assemblyIdentity);
            if (handleMethodId == null)
                continue;

            var key = (requestId, handleMethodId, EdgeKind.Handles.ToString());
            if (seen.Add(key))
            {
                edges.Add(new EdgeRecord(
                    sourceSymbolId: requestId,
                    targetSymbolId: handleMethodId,
                    kind: EdgeKind.Handles.ToString(),
                    provenance: "framework_derived",
                    snapshotId: snapshotId,
                    extractorVersion: Version));
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
