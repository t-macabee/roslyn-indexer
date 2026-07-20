using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class DirectCallersTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "directCallers";

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();
        var allowedKinds = new HashSet<string>
        {
            EdgeKind.Calls.ToString()
        };

        var traverser = new ImpactTraverser(context.EdgeStore, context.SnapshotId);
        var paths = traverser.TraceImpact(symbolId: context.SymbolId.Value, direction: ImpactDirection.Upstream, allowedEdgeKinds: allowedKinds, maxDepth: 1);

        var seen = new HashSet<string>();
        foreach (var path in paths)
        {
            foreach (var hop in path.Hops)
            {
                var neighborId = hop.SourceSymbolId;
                if (!seen.Add(neighborId))
                    continue;

                var item = context.BuildCapsuleItem(neighborId, hop.EdgeKind, hop.Provenance);
                if (item != null)
                {
                    results.Add(item);
                }
            }
        }

        var incomingEdges = context.EdgeStore.GetIncomingEdges(context.SnapshotId, context.SymbolId.Value);
        foreach (var edge in incomingEdges)
        {
            if (edge.Kind != EdgeKind.RoutesTo.ToString() &&
                edge.Kind != EdgeKind.Handles.ToString())
            {
                continue;
            }

            var sourceId = edge.SourceSymbolId;
            if (!seen.Add(sourceId))
                continue;

            var item = context.BuildCapsuleItem(sourceId, edge.Kind, edge.Provenance);
            if (item != null)
            {
                results.Add(item);
            }
        }

        return results;
    }
}
