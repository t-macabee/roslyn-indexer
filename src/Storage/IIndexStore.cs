#if CODE_ANALYSIS
using System.Diagnostics.CodeAnalysis;
#endif

namespace Lurp.Storage
{
    public sealed class SourceSearchResult
    {
        public string DocumentPath { get; }
        public string Snippet { get; }

        public SourceSearchResult(string documentPath, string snippet)
        {
            DocumentPath = documentPath ?? throw new ArgumentNullException(nameof(documentPath));
            Snippet = snippet ?? throw new ArgumentNullException(nameof(snippet));
        }
    }

    public sealed class SymbolSearchResult
    {
        public string SymbolId { get; }
        public string FullyQualifiedName { get; }
        public string Kind { get; }
        public string DocCommentId { get; }

        public SymbolSearchResult(string symbolId, string fullyQualifiedName, string kind, string docCommentId)
        {
            SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
            FullyQualifiedName = fullyQualifiedName ?? throw new ArgumentNullException(nameof(fullyQualifiedName));
            Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            DocCommentId = docCommentId ?? throw new ArgumentNullException(nameof(docCommentId));
        }
    }

    public enum ViewKind
    {
        Declaration,
        Signature,
        Body,
        Name,
        ContainingType,
        Surrounding,
    }

    public sealed class IndexedSymbolInfo
    {
        public SymbolId SymbolId { get; }
        public IndexedSymbolKind Kind { get; }
        public string? FullyQualifiedName { get; }
        public string? MetadataJson { get; }
        public int DeclarationCount { get; }
        public bool IsPartial { get; }

        public IndexedSymbolInfo(SymbolId symbolId,IndexedSymbolKind kind,string? fullyQualifiedName,string? metadataJson,int declarationCount,bool isPartial)
        {
            SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
            Kind = kind;
            FullyQualifiedName = fullyQualifiedName;
            MetadataJson = metadataJson;
            DeclarationCount = declarationCount;
            IsPartial = isPartial;
        }
    }

#if CODE_ANALYSIS
    [SuppressMessage("NDepend", "ND1200", Justification = "Composite interface that aggregates the five already-segregated store contracts (ISnapshotStore, IDeclarationStore, IEdgeStore, ISearchStore, ISemanticDiffStore) for multi-concern orchestrators. Each sub-interface is single-responsibility; this union is the deliberate one-injection-point facade, not an oversized interface.")]
#endif
    public interface IIndexStore : ISnapshotStore, IDeclarationStore, IEdgeStore, ISearchStore, ISemanticDiffStore
    {
    }
}

