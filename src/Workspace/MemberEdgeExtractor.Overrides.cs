using Lurp.Storage;
using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

public sealed partial class MemberEdgeExtractor
{
    private List<EdgeRecord> ExtractOverrides()
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var typeSymbol in GetNamespaceTypeMembers(_compilation.Assembly.GlobalNamespace))
        {
            foreach (var member in typeSymbol.GetMembers())
            {
                (string? sourceId, string? targetId) = member switch
                {
                    IMethodSymbol method when method.IsOverride && method.OverriddenMethod != null
                        => (MakeSymbolId(method), MakeSymbolId(method.OverriddenMethod)),
                    IPropertySymbol prop when prop.IsOverride && prop.OverriddenProperty != null
                        => (MakeSymbolId(prop), MakeSymbolId(prop.OverriddenProperty)),
                    _ => ((string?)null, (string?)null)
                };

                if (sourceId == null || targetId == null)
                    continue;

                var key = (sourceId, targetId, EdgeKind.Overrides.ToString());
                if (!seen.Add(key))
                    continue;

                var loc = GetMemberSourceLocation(member);
                edges.Add(MakeEdge(sourceId, targetId, EdgeKind.Overrides.ToString(),
                    ExtractorConstants.OverridesExtractor, loc));
            }
        }

        return edges;
    }
}
