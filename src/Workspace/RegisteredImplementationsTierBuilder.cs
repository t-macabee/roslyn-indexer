using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class RegisteredImplementationsTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "registeredImplementations";

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();
        var seen = new HashSet<string>();

        var incomingEdges = context.EdgeStore.GetIncomingEdges(context.SnapshotId, context.SymbolId.Value);
        foreach (var edge in incomingEdges)
        {
            if (edge.Kind != EdgeKind.MayDispatchTo.ToString() &&
                edge.Kind != EdgeKind.Registers.ToString())
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

        var outgoingEdges = context.EdgeStore.GetOutgoingEdges(context.SnapshotId, context.SymbolId.Value);
        foreach (var edge in outgoingEdges)
        {
            if (edge.Kind != EdgeKind.MayDispatchTo.ToString() &&
                edge.Kind != EdgeKind.Handles.ToString() &&
                edge.Kind != EdgeKind.Registers.ToString())
            {
                continue;
            }

            var targetId = edge.TargetSymbolId;
            if (!seen.Add(targetId))
                continue;

            var item = context.BuildCapsuleItem(targetId, edge.Kind, edge.Provenance);
            if (item != null)
            {
                results.Add(item);
            }
        }

        return results;
    }
}
