using Microsoft.CodeAnalysis;
using Lurp.Storage;

namespace Lurp.Workspace;

public sealed class SymbolExtractor
{
    private readonly SymbolExtractionContext _context;

    public SymbolExtractor(Compilation compilation, IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)> documentContents,
        IReadOnlyDictionary<DocumentId, DocumentVersionId> documentVersions,
        IReadOnlySet<DocumentId> generatedDocuments,
        string snapshotId,
        Action<string>? logWarning = null,
        IReadOnlySet<string>? scopeDocuments = null)
    {
        if (compilation == null) throw new ArgumentNullException(nameof(compilation));
        if (documentContents == null) throw new ArgumentNullException(nameof(documentContents));
        if (documentVersions == null) throw new ArgumentNullException(nameof(documentVersions));
        if (generatedDocuments == null) throw new ArgumentNullException(nameof(generatedDocuments));
        if (snapshotId == null) throw new ArgumentNullException(nameof(snapshotId));

        _context = new SymbolExtractionContext(compilation, documentContents, documentVersions, generatedDocuments, snapshotId, scopeDocuments);
        _logWarning = logWarning;
    }

    private readonly Action<string>? _logWarning;

    public (List<SymbolDeclaration> Declarations, int SkippedCount) ExtractAll() => new SymbolDeclarationExtractor(_context, _logWarning).ExtractAll();

    public List<EdgeRecord> ExtractEdges() => new SymbolStructuralEdgeExtractor(_context).ExtractEdges();
}
