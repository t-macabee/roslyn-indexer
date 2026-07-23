using Lurp.Storage;
using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class OverridesEdgeExtractor(MemberEdgeExtractionContext context) : IMemberEdgeExtractor
{
    List<EdgeRecord> IMemberEdgeExtractor.Extract()
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var typeSymbol in context.GetAllNamedTypes())
        {
            foreach (var member in typeSymbol.GetMembers())
            {
                if (!context.IsMemberInScope(member))
                    continue;

                (string? sourceId, string? targetId) = member switch
                {
                    IMethodSymbol method when method.IsOverride && method.OverriddenMethod != null
                        => (context.MakeSymbolId(method), context.MakeSymbolId(method.OverriddenMethod)),
                    IPropertySymbol prop when prop.IsOverride && prop.OverriddenProperty != null
                        => (context.MakeSymbolId(prop), context.MakeSymbolId(prop.OverriddenProperty)),
                    _ => ((string?)null, (string?)null)
                };

                if (sourceId == null || targetId == null)
                    continue;

                var key = (sourceId, targetId, EdgeKind.Overrides.ToString());
                if (!seen.Add(key))
                    continue;

                var loc = context.GetMemberSourceLocation(member);
                edges.Add(context.MakeEdge(sourceId, targetId, EdgeKind.Overrides.ToString(),
                    ExtractorConstants.OverridesExtractor, loc));
            }
        }

        return edges;
    }
}
