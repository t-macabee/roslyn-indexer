using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class ContractsTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "contracts";

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();
        var edges = context.EdgeStore.GetOutgoingEdges(context.SnapshotId, context.SymbolId.Value);

        foreach (var edge in edges)
        {
            if (edge.Kind != EdgeKind.Implements.ToString() &&
                edge.Kind != EdgeKind.Overrides.ToString())
            {
                continue;
            }

            var item = context.BuildCapsuleItem(edge.TargetSymbolId, edge.Kind, edge.Provenance);
            if (item != null)
            {
                results.Add(item);
            }
        }

        return results;
    }
}
