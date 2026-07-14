using Lurp.Storage;
using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp;

public sealed class PolymorphismExtractor
{
    private readonly Compilation _compilation;
    private readonly string _snapshotId;
    private readonly string _assemblyIdentity;

    public PolymorphismExtractor(
        Compilation compilation,
        string snapshotId)
    {
        _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        _snapshotId = snapshotId ?? throw new ArgumentNullException(nameof(snapshotId));
        _assemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
    }

    public List<EdgeRecord> ExtractAll()
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target)>();
        var allTypes = GetAllNamedTypes(_compilation.Assembly.GlobalNamespace);

        ExtractInterfaceDispatches(allTypes, edges, seen);
        ExtractVirtualOverrides(allTypes, edges, seen);

        return edges;
    }

    private void ExtractInterfaceDispatches(
        List<INamedTypeSymbol> allTypes,
        List<EdgeRecord> edges,
        HashSet<(string source, string target)> seen)
    {
        foreach (var type in allTypes)
        {
            if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct)
                continue;

            if (type.AllInterfaces.IsEmpty)
                continue;

            foreach (var iface in type.AllInterfaces)
            {
                foreach (var member in iface.GetMembers())
                {
                    if (member is not IMethodSymbol and not IPropertySymbol and not IEventSymbol)
                        continue;

                    var ifaceMemberId = MakeSymbolId(member);
                    if (ifaceMemberId == null)
                        continue;

                    var implMember = type.FindImplementationForInterfaceMember(member);
                    if (implMember == null)
                        continue;

                    var implMemberId = MakeSymbolId(implMember);
                    if (implMemberId == null || implMemberId == ifaceMemberId)
                        continue;

                    var key = (ifaceMemberId, implMemberId);
                    if (!seen.Add(key))
                        continue;

                    edges.Add(new EdgeRecord(
                        sourceSymbolId: ifaceMemberId,
                        targetSymbolId: implMemberId,
                        kind: EdgeKind.MayDispatchTo.ToString(),
                        provenance: "compiler_proved",
                        snapshotId: _snapshotId,
                        extractorVersion: "polymorphism-v1",
                        sourceDocumentPath: GetDocumentPath(implMember),
                        sourceStartLine: GetStartLine(implMember),
                        sourceStartColumn: GetStartColumn(implMember),
                        sourceEndLine: GetEndLine(implMember),
                        sourceEndColumn: GetEndColumn(implMember)));
                }
            }
        }
    }

    private void ExtractVirtualOverrides(
        List<INamedTypeSymbol> allTypes,
        List<EdgeRecord> edges,
        HashSet<(string source, string target)> seen)
    {
        foreach (var type in allTypes)
        {
            foreach (var member in type.GetMembers())
            {
                if (member is IMethodSymbol method && method.IsOverride && method.OverriddenMethod != null)
                {
                    var root = WalkToRootOverride(method);
                    var rootId = MakeSymbolId(root);
                    var overrideId = MakeSymbolId(method);
                    if (rootId == null || overrideId == null || rootId == overrideId)
                        continue;

                    var key = (rootId, overrideId);
                    if (!seen.Add(key))
                        continue;

                    edges.Add(MakeMayDispatchEdge(rootId, overrideId, method));
                }

                if (member is IPropertySymbol prop && prop.IsOverride && prop.OverriddenProperty != null)
                {
                    var root = WalkToRootOverride(prop);
                    var rootId = MakeSymbolId(root);
                    var overrideId = MakeSymbolId(prop);
                    if (rootId == null || overrideId == null || rootId == overrideId)
                        continue;

                    var key = (rootId, overrideId);
                    if (!seen.Add(key))
                        continue;

                    edges.Add(MakeMayDispatchEdge(rootId, overrideId, prop));
                }

                if (member is IEventSymbol evt && evt.IsOverride && evt.OverriddenEvent != null)
                {
                    var root = WalkToRootOverride(evt);
                    var rootId = MakeSymbolId(root);
                    var overrideId = MakeSymbolId(evt);
                    if (rootId == null || overrideId == null || rootId == overrideId)
                        continue;

                    var key = (rootId, overrideId);
                    if (!seen.Add(key))
                        continue;

                    edges.Add(MakeMayDispatchEdge(rootId, overrideId, evt));
                }
            }
        }
    }

    private static IMethodSymbol WalkToRootOverride(IMethodSymbol method)
    {
        var current = method;
        while (current.IsOverride && current.OverriddenMethod != null)
            current = current.OverriddenMethod;
        return current;
    }

    private static IPropertySymbol WalkToRootOverride(IPropertySymbol prop)
    {
        var current = prop;
        while (current.IsOverride && current.OverriddenProperty != null)
            current = current.OverriddenProperty;
        return current;
    }

    private static IEventSymbol WalkToRootOverride(IEventSymbol evt)
    {
        var current = evt;
        while (current.IsOverride && current.OverriddenEvent != null)
            current = current.OverriddenEvent;
        return current;
    }

    private EdgeRecord MakeMayDispatchEdge(string sourceId, string targetId, ISymbol targetSymbol)
    {
        return new EdgeRecord(
            sourceSymbolId: sourceId,
            targetSymbolId: targetId,
            kind: EdgeKind.MayDispatchTo.ToString(),
            provenance: "compiler_proved",
            snapshotId: _snapshotId,
            extractorVersion: "polymorphism-v1",
            sourceDocumentPath: GetDocumentPath(targetSymbol),
            sourceStartLine: GetStartLine(targetSymbol),
            sourceStartColumn: GetStartColumn(targetSymbol),
            sourceEndLine: GetEndLine(targetSymbol),
            sourceEndColumn: GetEndColumn(targetSymbol));
    }

    private string? MakeSymbolId(ISymbol symbol)
    {
        var docCommentId = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(docCommentId))
            return null;
        return $"{docCommentId}|{_assemblyIdentity}";
    }

    private static string? GetDocumentPath(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var path = syntaxRef.SyntaxTree?.FilePath;
        return string.IsNullOrEmpty(path) ? null : path.Replace('\\', '/');
    }

    private static int? GetStartLine(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var span = syntaxRef.GetSyntax().GetLocation().GetLineSpan();
        return span.StartLinePosition.Line;
    }

    private static int? GetStartColumn(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var span = syntaxRef.GetSyntax().GetLocation().GetLineSpan();
        return span.StartLinePosition.Character;
    }

    private static int? GetEndLine(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var span = syntaxRef.GetSyntax().GetLocation().GetLineSpan();
        return span.EndLinePosition.Line;
    }

    private static int? GetEndColumn(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var span = syntaxRef.GetSyntax().GetLocation().GetLineSpan();
        return span.EndLinePosition.Character;
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
            {
                types.Add(nested);
            }
        }

        foreach (var childNs in ns.GetNamespaceMembers())
        {
            CollectTypes(childNs, types);
        }
    }
}
