using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class SurroundingSiblingsTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "surroundingSource";

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();

        var incomingEdges = context.EdgeStore.GetIncomingEdges(context.SnapshotId, context.SymbolId.Value);
        string? parentId = null;
        foreach (var edge in incomingEdges)
        {
            if (edge.Kind == EdgeKind.Contains.ToString())
            {
                parentId = edge.SourceSymbolId;
                break;
            }
        }

        if (parentId == null)
            return results;

        var parentEdges = context.EdgeStore.GetOutgoingEdges(context.SnapshotId, parentId);
        foreach (var edge in parentEdges)
        {
            if (edge.Kind != EdgeKind.Contains.ToString())
                continue;

            var siblingId = edge.TargetSymbolId;
            if (siblingId == context.SymbolId.Value)
                continue;

            var item = context.BuildCapsuleItem(siblingId, EdgeKind.Contains.ToString(), edge.Provenance);
            if (item != null)
            {
                results.Add(item);
            }
        }

        return results;
    }
}
