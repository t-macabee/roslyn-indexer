using System;

namespace RoslynIndexer.Storage
{
    public interface IIndexStore
    {
        void Open(string dbPath);
        void Close();
        bool IsOpen { get; }
        void RunMigrations();
        int GetCurrentSchemaVersion();
        void ValidateSchema(int expectedVersion);
        void SaveWorkspace(WorkspaceId id, string gitRoot, string solutionPath, DateTime createdAtUtc);
        void SaveSnapshot(SnapshotManifest manifest);
        SnapshotManifest? LoadLatestSnapshot(WorkspaceId workspaceId);
    }
}