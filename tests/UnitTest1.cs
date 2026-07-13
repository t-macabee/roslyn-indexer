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
    public void RunMigrations_AppliesBothMigrations_SchemaVersionIsTwo()
    {
        var runner = new MigrationRunner(_dbPath);

        runner.RunMigrations();

        Assert.Equal(2, runner.GetCurrentSchemaVersion());
    }

    [Fact]
    public void RunMigrations_CalledTwice_IsIdempotent()
    {
        var runner = new MigrationRunner(_dbPath);

        runner.RunMigrations();
        runner.RunMigrations();

        Assert.Equal(2, runner.GetCurrentSchemaVersion());
    }

    [Fact]
    public void Migration002_AddsLineStartsColumn()
    {
        var runner = new MigrationRunner(_dbPath);
        runner.RunMigrations();

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(document_versions);";
        using var reader = cmd.ExecuteReader();
        bool found = false;
        while (reader.Read())
        {
            if (reader.GetString(1) == "line_starts")
                found = true;
        }
        Assert.True(found, "line_starts column should exist after migration 002");
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

    [Fact]
    public void SaveAndLoad_ContentRoundTrips()
    {
        var store = new SqliteIndexStore(_dbPath);
        store.Open(_dbPath);
        store.RunMigrations();

        var snapshotId = "snap-content-001";
        var workspaceId = "workspace:///root/proj";
        var sourceBytes = System.Text.Encoding.UTF8.GetBytes("using System;\n\nclass Foo { }\n");
        var lineStarts = "[0,13,15]";

        var original = new SnapshotManifest(
            snapshotId: snapshotId,
            workspaceId: workspaceId,
            gitRoot: "/root",
            solutionPath: "/root/proj",
            sdkVersion: "10.0.301",
            compilerVersion: "4.12.0.0",
            createdAtUtc: DateTime.UtcNow,
            documents: new System.Collections.Generic.List<DocumentVersion>
            {
                new("doc1", "src/Foo.cs", "hash1", "utf-8", lineStarts, DateTime.MinValue,
                    sourceBytes, lineStarts),
            }
        );

        store.SaveSnapshot(original);

        
        var source = store.GetSource("src/Foo.cs", snapshotId);
        Assert.NotNull(source);
        Assert.Equal("using System;\n\nclass Foo { }\n", source);

        store.Close();
    }

    [Fact]
    public void GetSource_MissingDocument_ReturnsNull()
    {
        var store = new SqliteIndexStore(_dbPath);
        store.Open(_dbPath);
        store.RunMigrations();

        var result = store.GetSource("nonexistent.cs", "snap-none");

        Assert.Null(result);

        store.Close();
    }

    [Fact]
    public void GetSource_MissingSnapshot_ReturnsNull()
    {
        var store = new SqliteIndexStore(_dbPath);
        store.Open(_dbPath);
        store.RunMigrations();

        var result = store.GetSource("src/Foo.cs", "non-existent-snapshot");

        Assert.Null(result);

        store.Close();
    }

    [Fact]
    public void GetSource_NoRoslyn_ReturnsContentFromSqliteOnly()
    {
        
        var store = new SqliteIndexStore(_dbPath);
        store.Open(_dbPath);
        store.RunMigrations();

        var snapshotId = "snap-noroslyn-001";
        var workspaceId = "workspace:///root/proj";
        var sourceBytes = System.Text.Encoding.UTF8.GetBytes("console.log('hello');");
        var lineStarts = "[0,22]";

        var original = new SnapshotManifest(
            snapshotId: snapshotId,
            workspaceId: workspaceId,
            gitRoot: "/root",
            solutionPath: "/root/proj",
            sdkVersion: "10.0.301",
            compilerVersion: "4.12.0.0",
            createdAtUtc: DateTime.UtcNow,
            documents: new System.Collections.Generic.List<DocumentVersion>
            {
                new("doc1", "src/app.cs", "hash1", "utf-8", lineStarts, DateTime.MinValue,
                    sourceBytes, lineStarts),
            }
        );
        store.SaveSnapshot(original);
        store.Close();

        
        var reopened = new SqliteIndexStore(_dbPath);
        reopened.Open(_dbPath);
        
        var source = reopened.GetSource("src/app.cs", snapshotId);
        Assert.NotNull(source);
        Assert.Equal("console.log('hello');", source);

        reopened.Close();
    }

    [Fact]
    public void LineStarts_FirstOffsetIsZero()
    {
        
        var store = new SqliteIndexStore(_dbPath);
        store.Open(_dbPath);
        store.RunMigrations();

        var snapshotId = "snap-linestarts-001";
        var workspaceId = "workspace:///root/proj";
        var sourceBytes = System.Text.Encoding.UTF8.GetBytes("line1\nline2\nline3\n");
        var lineStarts = "[0,6,12,18]";

        var original = new SnapshotManifest(
            snapshotId: snapshotId,
            workspaceId: workspaceId,
            gitRoot: "/root",
            solutionPath: "/root/proj",
            sdkVersion: "10.0.301",
            compilerVersion: "4.12.0.0",
            createdAtUtc: DateTime.UtcNow,
            documents: new System.Collections.Generic.List<DocumentVersion>
            {
                new("doc1", "src/multi.cs", "hash1", "utf-8", lineStarts, DateTime.MinValue,
                    sourceBytes, lineStarts),
            }
        );
        store.SaveSnapshot(original);

        
        var loaded = store.LoadLatestSnapshot(new Storage.WorkspaceId(workspaceId));
        Assert.NotNull(loaded);
        var doc = loaded!.Documents[0];
        Assert.Equal("[0,6,12,18]", doc.LineStart);

        store.Close();
    }

    [Fact]
    public void Content_WithNullContent_StoresNull()
    {
        var store = new SqliteIndexStore(_dbPath);
        store.Open(_dbPath);
        store.RunMigrations();

        var snapshotId = "snap-nullcontent-001";
        var workspaceId = "workspace:///root/proj";

        var original = new SnapshotManifest(
            snapshotId: snapshotId,
            workspaceId: workspaceId,
            gitRoot: "/root",
            solutionPath: "/root/proj",
            sdkVersion: "10.0.301",
            compilerVersion: "4.12.0.0",
            createdAtUtc: DateTime.UtcNow,
            documents: new System.Collections.Generic.List<DocumentVersion>
            {
                
                new("doc1", "src/empty.cs", "hash1", "utf-8", "", DateTime.MinValue),
            }
        );
        store.SaveSnapshot(original);

        
        var source = store.GetSource("src/empty.cs", snapshotId);
        Assert.Null(source);

        store.Close();
    }

    [Fact]
    public void Migration002_AppliedOnExistingMigration001_Database()
    {
        
        var runner = new MigrationRunner(_dbPath);
        runner.RunMigrations();
        Assert.Equal(2, runner.GetCurrentSchemaVersion());

        
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(document_versions);";
            using var reader = cmd.ExecuteReader();
            bool found = false;
            while (reader.Read())
            {
                if (reader.GetString(1) == "line_starts")
                    found = true;
            }
            Assert.True(found, "line_starts column must exist after upgrade path");
        }
    }
}
