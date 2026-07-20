using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class DirectCalleesTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "directCallees";

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();
        var allowedKinds = new HashSet<string>
        {
            EdgeKind.Calls.ToString(),
            EdgeKind.Constructs.ToString()
        };

        var traverser = new ImpactTraverser(context.EdgeStore, context.SnapshotId);
        var paths = traverser.TraceImpact(symbolId: context.SymbolId.Value, direction: ImpactDirection.Downstream, allowedEdgeKinds: allowedKinds, maxDepth: 1);

        var seen = new HashSet<string>();
        foreach (var path in paths)
        {
            foreach (var hop in path.Hops)
            {
                var neighborId = hop.TargetSymbolId;
                if (!seen.Add(neighborId))
                    continue;

                var item = context.BuildCapsuleItem(neighborId, hop.EdgeKind, hop.Provenance);
                if (item != null)
                {
                    results.Add(item);
                }
            }
        }

        return results;
    }
}
