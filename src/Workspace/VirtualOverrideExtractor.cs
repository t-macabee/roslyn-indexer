using Microsoft.CodeAnalysis;
using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

/// <summary>
/// For every override, walk to the root virtual declaration and emit a
/// may_dispatch_to edge from the root to the override.  Because the
/// override chain is fully resolved at compile time within a single
/// compilation, all such edges are "compiler_proved".
/// </summary>
internal sealed class VirtualOverrideExtractor(PolymorphismExtractionContext context)
{
    internal List<EdgeRecord> Extract(List<INamedTypeSymbol> allTypes)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var type in allTypes)
        {
            foreach (var member in type.GetMembers())
            {
                switch (member)
                {
                    case IMethodSymbol method when method.IsOverride && method.OverriddenMethod != null:
                        TryEmitOverrideEdge(WalkToRootOverride(method), method, edges, seen);
                        break;
                    case IPropertySymbol prop when prop.IsOverride && prop.OverriddenProperty != null:
                        TryEmitOverrideEdge(WalkToRootOverride(prop), prop, edges, seen);
                        break;
                    case IEventSymbol evt when evt.IsOverride && evt.OverriddenEvent != null:
                        TryEmitOverrideEdge(WalkToRootOverride(evt), evt, edges, seen);
                        break;
                }
            }
        }

        return edges;
    }

    private void TryEmitOverrideEdge(ISymbol root, ISymbol overrideMember, List<EdgeRecord> edges, HashSet<(string source, string target, string kind)> seen)
    {
        var rootId = context.MakeSymbolId(root);
        var overrideId = context.MakeSymbolId(overrideMember);
        if (rootId == null || overrideId == null || rootId == overrideId)
            return;

        var key = (rootId, overrideId, EdgeKind.MayDispatchTo.ToString());
        if (!seen.Add(key))
            return;

        edges.Add(context.MakeMayDispatchEdge(rootId, overrideId, overrideMember, "compiler_proved"));
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
}
