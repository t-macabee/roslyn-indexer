namespace Lurp.Storage
{
    public interface ISnapshotStore
    {
        void Open(string dbPath);
        void Close();
        bool IsOpen { get; }
        void RunMigrations();
        int GetCurrentSchemaVersion();
        void ValidateSchema(int expectedVersion);

        void SaveWorkspace(string id, string gitRoot, string solutionPath, DateTime createdAtUtc);
        void SaveSnapshot(SnapshotRow manifest);
        void MarkSnapshotInProgress(string snapshotId);
        void MarkSnapshotComplete(string snapshotId);
        SnapshotRow? LoadLatestSnapshot(string workspaceId);
        string? GetLatestSnapshotId(string? workspaceId = null);
        string? GetSource(string relativePath, string snapshotId);
        List<string> GetSnapshotIds(string workspaceId);

        void SaveSnapshotDocuments(string snapshotId, IEnumerable<(string DocumentId, string DocumentVersionId)> entries);
        Dictionary<string, string> GetDocumentVersionIdsByPath(string snapshotId);
        List<string> GetDocumentVersionIdsForDocuments(string snapshotId, IEnumerable<string> documentPaths);

        void SaveSnapshotSymbols(string snapshotId, IEnumerable<string> symbolIds);
        void CopySnapshotSymbols(string fromSnapshotId, string toSnapshotId);
        void DeleteSnapshotSymbolsBySymbolIds(string snapshotId, IEnumerable<string> symbolIds);
        List<string> GetSymbolIdsInSnapshot(string snapshotId);

        void PruneOldSnapshots(int keep = 3);

        void SaveTimings(string snapshotId, IEnumerable<SnapshotTimingRow> timings);
        List<SnapshotTimingRow> GetTimings(string snapshotId);
    }
}
