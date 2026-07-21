using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

public sealed class DeclarationStore : IDeclarationStore
{
    private readonly DeclarationWriteStore _writer;
    private readonly DeclarationReadStore _reader;
    private readonly DeclarationMaintenanceStore _maintenance;

    public DeclarationStore(string dbPath)
    {
        if (dbPath == null) throw new ArgumentNullException(nameof(dbPath));
        _writer = new DeclarationWriteStore(dbPath);
        _reader = new DeclarationReadStore(dbPath);
        _maintenance = new DeclarationMaintenanceStore(dbPath);
    }

    public void SaveDeclarations(string snapshotId, IEnumerable<SymbolDeclaration> declarations)
        => _writer.SaveDeclarations(snapshotId, declarations);

    public IndexedSymbolInfo? GetSymbolInfo(string symbolId, string snapshotId)
        => _reader.GetSymbolInfo(symbolId, snapshotId);

    public string? GetSymbolSource(string symbolId, string snapshotId, ViewKind viewKind, bool includeGenerated = false)
        => _reader.GetSymbolSource(symbolId, snapshotId, viewKind, includeGenerated);

    public string? GetContainingTypeSource(string symbolId, string snapshotId)
        => _reader.GetContainingTypeSource(symbolId, snapshotId);

    public string? GetSurroundingLines(string symbolId, string snapshotId, int contextLines)
        => _reader.GetSurroundingLines(symbolId, snapshotId, contextLines);

    public void DeleteDeclarationsByDocumentVersionIds(IEnumerable<string> documentVersionIds)
        => _maintenance.DeleteDeclarationsByDocumentVersionIds(documentVersionIds);

    public List<string> GetSymbolIdsByDocumentVersionIds(string snapshotId, IEnumerable<string> documentVersionIds)
        => _maintenance.GetSymbolIdsByDocumentVersionIds(snapshotId, documentVersionIds);

    public string? ResolveSymbolByLocation(string relativePath, int line, string snapshotId, bool includeGenerated = false)
        => _maintenance.ResolveSymbolByLocation(relativePath, line, snapshotId, includeGenerated);

    internal static IndexedSymbolInfo? ReadSymbolInfo(SqliteDataReader reader)
    {
        var sid = new SymbolId(docCommentId: reader.GetString(1),
            assemblyIdentity: reader.GetString(2),
            fullyQualifiedName: reader.IsDBNull(4) ? null : reader.GetString(4));

        var kindStr = reader.GetString(3);
        Enum.TryParse<IndexedSymbolKind>(kindStr, ignoreCase: true, out var kind);

        return new IndexedSymbolInfo(symbolId: sid, kind: kind, fullyQualifiedName: reader.IsDBNull(4) ? null : reader.GetString(4),
            metadataJson: reader.IsDBNull(5) ? null : reader.GetString(5),
            declarationCount: reader.GetInt32(6),
            isPartial: !reader.IsDBNull(7) && reader.GetInt32(7) == 1);
    }
}
