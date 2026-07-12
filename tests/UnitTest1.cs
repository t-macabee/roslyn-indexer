using System;
using System.IO;
using Microsoft.Data.Sqlite;
using RoslynIndexer.Storage;

namespace RoslynIndexer.Storage.Tests;

public class MigrationRunnerTests : IDisposable
{
    private readonly string _dbPath;

    public MigrationRunnerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_test_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public void RunMigrations_AppliesMigration001_SchemaVersionIsOne()
    {
        var runner = new MigrationRunner(_dbPath);

        runner.RunMigrations();

        Assert.Equal(1, runner.GetCurrentSchemaVersion());
    }

    [Fact]
    public void RunMigrations_CalledTwice_IsIdempotent()
    {
        var runner = new MigrationRunner(_dbPath);

        runner.RunMigrations();
        runner.RunMigrations();

        Assert.Equal(1, runner.GetCurrentSchemaVersion());
    }

    [Fact]
    public void SaveAndLoadLatestSnapshot_RoundTripsFields()
    {
        var store = new SqliteIndexStore(_dbPath);
        store.Open(_dbPath);
        store.RunMigrations();

        var snapshotId = "test-snap-001";
        var workspaceId = "workspace:///home/user/project/src/sln";
        var gitRoot = "/home/user/project";
        var solutionPath = "/home/user/project/src/sln";
        var createdAt = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        var original = new SnapshotManifest(
            snapshotId: snapshotId,
            workspaceId: workspaceId,
            gitRoot: gitRoot,
            solutionPath: solutionPath,
            sdkVersion: "10.0.301",
            compilerVersion: "4.12.0.0",
            createdAtUtc: createdAt,
            documents: new System.Collections.Generic.List<DocumentVersion>
            {
                new("doc1", "src/Program.cs", "abc123", "utf-8", "", DateTime.MinValue),
                new("doc2", "src/Utils.cs", "def456", "utf-8", "", DateTime.MinValue),
            }
        );

        store.SaveSnapshot(original);

        var loaded = store.LoadLatestSnapshot(new Storage.WorkspaceId(workspaceId));

        Assert.NotNull(loaded);
        Assert.Equal(snapshotId, loaded!.SnapshotId);
        Assert.Equal(workspaceId, loaded.WorkspaceId);
        Assert.Equal(gitRoot, loaded.GitRoot);
        Assert.Equal(solutionPath, loaded.SolutionPath);
        Assert.Equal("10.0.301", loaded.SdkVersion);
        Assert.Equal("4.12.0.0", loaded.CompilerVersion);
        Assert.Equal(createdAt, loaded.CreatedAtUtc);
        Assert.Equal(2, loaded.Documents.Count);

        
        var doc1 = loaded.Documents[0];
        Assert.Equal("doc1", doc1.DocumentId);
        Assert.Equal("src/Program.cs", doc1.FilePath);
        Assert.Equal("abc123", doc1.ContentHash);

        var doc2 = loaded.Documents[1];
        Assert.Equal("doc2", doc2.DocumentId);
        Assert.Equal("src/Utils.cs", doc2.FilePath);
        Assert.Equal("def456", doc2.ContentHash);

        store.Close();
    }

    [Fact]
    public void LoadLatestSnapshot_NoSnapshot_ReturnsNull()
    {
        var store = new SqliteIndexStore(_dbPath);
        store.Open(_dbPath);
        store.RunMigrations();

        var result = store.LoadLatestSnapshot(new Storage.WorkspaceId("workspace:///nonexistent"));

        Assert.Null(result);

        store.Close();
    }
}
