using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class SecondDegreeContextTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "secondDegreeContext";

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();
        var allowedKinds = new HashSet<string>
        {
            EdgeKind.Calls.ToString()
        };

        if (context.MaxHops <= 1)
            return results;

        var traverser = new ImpactTraverser(context.EdgeStore, context.SnapshotId);
        var paths = traverser.TraceImpact(symbolId: context.SymbolId.Value, direction: ImpactDirection.Upstream, allowedEdgeKinds: allowedKinds, maxDepth: context.MaxHops);

        var seen = new HashSet<string>();
        foreach (var path in paths)
        {
            foreach (var hop in path.Hops)
            {
                var neighborId = hop.SourceSymbolId;
                if (!seen.Add(neighborId))
                    continue;

                if (neighborId == context.SymbolId.Value)
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
