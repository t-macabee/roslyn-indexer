using Lurp.Storage;

namespace Lurp.Workspace;

internal sealed class ContextTierContext(IEdgeStore edgeStore, IDeclarationStore declarationStore, string snapshotId, SymbolId symbolId, int maxHops, bool includeGenerated)
{
    internal IEdgeStore EdgeStore { get; } = edgeStore;
    internal IDeclarationStore DeclarationStore { get; } = declarationStore;
    internal string SnapshotId { get; } = snapshotId;
    internal SymbolId SymbolId { get; } = symbolId;
    internal int MaxHops { get; } = maxHops;
    internal bool IncludeGenerated { get; } = includeGenerated;

    internal CapsuleItem? BuildCapsuleItem(string symbolId, string edgeKind, string provenance)
    {
        var info = DeclarationStore.GetSymbolInfo(symbolId, SnapshotId);
        if (info == null)
            return null;

        var source = DeclarationStore.GetSymbolSource(symbolId, SnapshotId, ViewKind.Declaration, IncludeGenerated);

        if (!IncludeGenerated && source == null)
        {
            var hasGeneratedOnly = DeclarationStore.GetSymbolSource(symbolId, SnapshotId, ViewKind.Declaration, true) != null;
            if (hasGeneratedOnly)
                return null;
        }

        return new CapsuleItem(symbolId: symbolId, kind: info.Kind.ToString(),
            fullyQualifiedName: info.FullyQualifiedName ?? symbolId,
            provenance: provenance,
            edgeKind: edgeKind,
            source: source);
    }
}
