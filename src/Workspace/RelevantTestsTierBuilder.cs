using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class RelevantTestsTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "relevantTests";

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();

        var incomingEdges = context.EdgeStore.GetIncomingEdges(context.SnapshotId, context.SymbolId.Value);
        foreach (var edge in incomingEdges)
        {
            if (edge.Kind != EdgeKind.TestedBy.ToString())
                continue;

            var testSymbolId = edge.SourceSymbolId;
            var item = context.BuildCapsuleItem(testSymbolId, edge.Kind, edge.Provenance);
            if (item != null)
            {
                results.Add(item);
            }
        }

        return results;
    }
}
