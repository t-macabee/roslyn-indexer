#if CODE_ANALYSIS
using System.Diagnostics.CodeAnalysis;
#endif

namespace Lurp.Storage;

#if CODE_ANALYSIS
[SuppressMessage("NDepend", "ND1001", Justification = "Facade that delegates to six inner stores (SnapshotLifecycleStore, SnapshotDocumentStore, SnapshotSymbolStore, SnapshotPruner, SnapshotTimingStore, etc.). 23 methods are one-line pass-throughs to specialized sub-stores.")]
#endif
public sealed class SnapshotStore : ISnapshotStore
{
    private readonly string _dbPath;
    private readonly SnapshotLifecycleStore _lifecycle;
    private readonly SnapshotDocumentStore _documents;
    private readonly SnapshotSymbolStore _symbols;
    private readonly SnapshotPruner _pruner;
    private readonly SnapshotTimingStore _timings;

    public SnapshotStore(string dbPath)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _lifecycle = new SnapshotLifecycleStore(dbPath);
        _documents = new SnapshotDocumentStore(dbPath);
        _symbols = new SnapshotSymbolStore(dbPath);
        _pruner = new SnapshotPruner(dbPath);
        _timings = new SnapshotTimingStore(dbPath);
    }

    public bool IsOpen { get; private set; }

    public void Open(string dbPath)
    {
        // No-op; connections are created per-method. Kept for interface compat.
    }

    public void Close()
    {
        // No-op; kept for interface compat.
    }

    public void RunMigrations() => new MigrationRunner(_dbPath).RunMigrations();

    public int GetCurrentSchemaVersion() => new MigrationRunner(_dbPath).GetCurrentSchemaVersion();

    public void ValidateSchema(int expectedVersion)
    {
        var actual = GetCurrentSchemaVersion();
        if (actual != expectedVersion)
            throw new InvalidOperationException($"Schema version mismatch: expected {expectedVersion}, got {actual}.");
    }

    public void SaveWorkspace(string id, string gitRoot, string solutionPath, DateTime createdAtUtc)
        => _lifecycle.SaveWorkspace(id, gitRoot, solutionPath, createdAtUtc);

    public void SaveSnapshot(SnapshotRow manifest) => _lifecycle.SaveSnapshot(manifest);

    public void MarkSnapshotInProgress(string snapshotId) => _lifecycle.MarkSnapshotInProgress(snapshotId);

    public void MarkSnapshotComplete(string snapshotId) => _lifecycle.MarkSnapshotComplete(snapshotId);

    public SnapshotRow? LoadLatestSnapshot(string workspaceId) => _lifecycle.LoadLatestSnapshot(workspaceId);

    public string? GetLatestSnapshotId(string? workspaceId = null) => _lifecycle.GetLatestSnapshotId(workspaceId);

    public List<string> GetSnapshotIds(string workspaceId) => _lifecycle.GetSnapshotIds(workspaceId);

    public string? GetSource(string relativePath, string snapshotId) => _documents.GetSource(relativePath, snapshotId);

    public void SaveSnapshotDocuments(string snapshotId, IEnumerable<(string DocumentId, string DocumentVersionId)> entries)
        => _documents.SaveSnapshotDocuments(snapshotId, entries);

    public Dictionary<string, string> GetDocumentVersionIdsByPath(string snapshotId)
        => _documents.GetDocumentVersionIdsByPath(snapshotId);

    public List<string> GetDocumentVersionIdsForDocuments(string snapshotId, IEnumerable<string> documentPaths)
        => _documents.GetDocumentVersionIdsForDocuments(snapshotId, documentPaths);

    public void SaveSnapshotSymbols(string snapshotId, IEnumerable<string> symbolIds)
        => _symbols.SaveSnapshotSymbols(snapshotId, symbolIds);

    public void CopySnapshotSymbols(string fromSnapshotId, string toSnapshotId)
        => _symbols.CopySnapshotSymbols(fromSnapshotId, toSnapshotId);

    public void DeleteSnapshotSymbolsBySymbolIds(string snapshotId, IEnumerable<string> symbolIds)
        => _symbols.DeleteSnapshotSymbolsBySymbolIds(snapshotId, symbolIds);

    public List<string> GetSymbolIdsInSnapshot(string snapshotId) => _symbols.GetSymbolIdsInSnapshot(snapshotId);

    public void PruneOldSnapshots(int keep = 3) => _pruner.PruneOldSnapshots(keep);

    public void SaveTimings(string snapshotId, IEnumerable<SnapshotTimingRow> timings)
        => _timings.SaveTimings(snapshotId, timings);

    public List<SnapshotTimingRow> GetTimings(string snapshotId)
        => _timings.GetTimings(snapshotId);
}
