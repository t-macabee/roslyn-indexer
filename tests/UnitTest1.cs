using Lurp.Adapters;
using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using System.Text;
using DocumentId = Lurp.Workspace.DocumentId;

namespace Lurp.Storage.Tests;

public class MigrationRunnerTests : IDisposable
{
    private readonly string _dbPath;
    private SqliteIndexStore? _store;

    public MigrationRunnerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_test_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        _store?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    internal static SymbolDeclaration MakeDecl(
        string docCommentId,
        string assembly,
        IndexedSymbolKind kind,
        string docVersionId,
        int? fullS, int? fullE,
        int? sigS, int? sigE,
        int? bodyS, int? bodyE,
        int? nameS, int? nameE,
        bool isPartial = false,
        string? fqn = null,
        string? metadataJson = null,
        string? symbolId = null)
    {
        return new SymbolDeclaration
        {
            SymbolId = symbolId != null
            ? new SymbolId(SymbolId.Parse(symbolId).DocCommentId, SymbolId.Parse(symbolId).AssemblyIdentity, fqn)
            : new SymbolId(docCommentId, assembly, fqn),
            Kind = kind,
            DocumentVersionId = docVersionId,
            FullSpan = new DeclarationSpan(fullS, fullE),
            SignatureSpan = new DeclarationSpan(sigS, sigE),
            BodySpan = new DeclarationSpan(bodyS, bodyE),
            NameSpan = new DeclarationSpan(nameS, nameE),
            IsPartial = isPartial,
            MetadataJson = metadataJson
        };
    }

    [Fact]
    public void RunMigrations_AppliesAllMigrations_SchemaVersionIsFifteen()
    {
        var runner = new MigrationRunner(_dbPath);

        runner.RunMigrations();

        Assert.Equal(15, runner.GetCurrentSchemaVersion());
    }

    [Fact]
    public void RunMigrations_CalledTwice_IsIdempotent()
    {
        var runner = new MigrationRunner(_dbPath);

        runner.RunMigrations();
        runner.RunMigrations();

        Assert.Equal(15, runner.GetCurrentSchemaVersion());
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
        _store = store;
        store.Open(_dbPath);
        store.RunMigrations();

        var snapshotId = "test-snap-001";
        var workspaceId = "workspace:///home/user/project/src/sln";
        var gitRoot = "/home/user/project";
        var solutionPath = "/home/user/project/src/sln";
        var createdAt = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        var original = new SnapshotRow
        {
            SnapshotId = snapshotId,
            WorkspaceId = workspaceId,
            GitRoot = gitRoot,
            SolutionPath = solutionPath,
            SdkVersion = "10.0.301",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = createdAt,
            Documents = new System.Collections.Generic.List<DocumentVersion>
            {
                new() { DocumentId = "doc1", FilePath = "src/Program.cs", ContentHash = "abc123", Encoding = "utf-8", LineStart = "", CreatedAtUtc = DateTime.MinValue },
                new() { DocumentId = "doc2", FilePath = "src/Utils.cs", ContentHash = "def456", Encoding = "utf-8", LineStart = "", CreatedAtUtc = DateTime.MinValue },
            }
        };

        store.SaveSnapshot(original);
        store.MarkSnapshotComplete(snapshotId);

        var loaded = store.LoadLatestSnapshot(workspaceId);

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
        _store = store;
        store.Open(_dbPath);
        store.RunMigrations();

        var result = store.LoadLatestSnapshot("workspace:///nonexistent");

        Assert.Null(result);

        store.Close();
    }

    [Fact]
    public void SaveAndLoad_ContentRoundTrips()
    {
        var store = new SqliteIndexStore(_dbPath);
        _store = store;
        store.Open(_dbPath);
        store.RunMigrations();

        var snapshotId = "snap-content-001";
        var workspaceId = "workspace:///root/proj";
        var sourceBytes = System.Text.Encoding.UTF8.GetBytes("using System;\n\nclass Foo { }\n");
        var lineStarts = "[0,13,15]";

        var original = new SnapshotRow
        {
            SnapshotId = snapshotId,
            WorkspaceId = workspaceId,
            GitRoot = "/root",
            SolutionPath = "/root/proj",
            SdkVersion = "10.0.301",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = DateTime.UtcNow,
            Documents = new System.Collections.Generic.List<DocumentVersion>
            {
                new DocumentVersion(sourceBytes) { DocumentId = "doc1", FilePath = "src/Foo.cs", ContentHash = "hash1", Encoding = "utf-8", LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue, LineStarts = lineStarts },
            }
        };

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
        _store = store;
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
        _store = store;
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
        _store = store;
        store.Open(_dbPath);
        store.RunMigrations();

        var snapshotId = "snap-noroslyn-001";
        var workspaceId = "workspace:///root/proj";
        var sourceBytes = System.Text.Encoding.UTF8.GetBytes("console.log('hello');");
        var lineStarts = "[0,22]";

        var original = new SnapshotRow
        {
            SnapshotId = snapshotId,
            WorkspaceId = workspaceId,
            GitRoot = "/root",
            SolutionPath = "/root/proj",
            SdkVersion = "10.0.301",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = DateTime.UtcNow,
            Documents = new System.Collections.Generic.List<DocumentVersion>
            {
                new DocumentVersion(sourceBytes) { DocumentId = "doc1", FilePath = "src/app.cs", ContentHash = "hash1", Encoding = "utf-8", LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue, LineStarts = lineStarts },
            }
        };
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
        _store = store;
        store.Open(_dbPath);
        store.RunMigrations();

        var snapshotId = "snap-linestarts-001";
        var workspaceId = "workspace:///root/proj";
        var sourceBytes = System.Text.Encoding.UTF8.GetBytes("line1\nline2\nline3\n");
        var lineStarts = "[0,6,12,18]";

        var original = new SnapshotRow
        {
            SnapshotId = snapshotId,
            WorkspaceId = workspaceId,
            GitRoot = "/root",
            SolutionPath = "/root/proj",
            SdkVersion = "10.0.301",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = DateTime.UtcNow,
            Documents = new System.Collections.Generic.List<DocumentVersion>
            {
                new DocumentVersion(sourceBytes) { DocumentId = "doc1", FilePath = "src/multi.cs", ContentHash = "hash1", Encoding = "utf-8", LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue, LineStarts = lineStarts },
            }
        };
        store.SaveSnapshot(original);
        store.MarkSnapshotComplete(snapshotId);

        var loaded = store.LoadLatestSnapshot(workspaceId);
        Assert.NotNull(loaded);
        var doc = loaded!.Documents[0];
        Assert.Equal("[0,6,12,18]", doc.LineStart);

        store.Close();
    }

    [Fact]
    public void Content_WithNullContent_StoresNull()
    {
        var store = new SqliteIndexStore(_dbPath);
        _store = store;
        store.Open(_dbPath);
        store.RunMigrations();

        var snapshotId = "snap-nullcontent-001";
        var workspaceId = "workspace:///root/proj";

        var original = new SnapshotRow
        {
            SnapshotId = snapshotId,
            WorkspaceId = workspaceId,
            GitRoot = "/root",
            SolutionPath = "/root/proj",
            SdkVersion = "10.0.301",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = DateTime.UtcNow,
            Documents = new System.Collections.Generic.List<DocumentVersion>
            {

                new() { DocumentId = "doc1", FilePath = "src/empty.cs", ContentHash = "hash1", Encoding = "utf-8", LineStart = "", CreatedAtUtc = DateTime.MinValue },
            }
        };
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
        Assert.Equal(15, runner.GetCurrentSchemaVersion());

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
        Assert.True(found, "line_starts column must exist after upgrade path");
    }

    [Fact]
    public void Migration010_AddsLastChangedSnapshotIdColumn()
    {
        var runner = new MigrationRunner(_dbPath);
        runner.RunMigrations();

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(documents);";
        using var reader = cmd.ExecuteReader();
        bool found = false;
        while (reader.Read())
        {
            if (reader.GetString(1) == "last_changed_snapshot_id")
                found = true;
        }
        Assert.True(found, "last_changed_snapshot_id column should exist after migration 010");
    }

    public class SymbolStoreTests : IDisposable
    {
        private readonly string _dbPath;
        private SqliteIndexStore? _store;

        public SymbolStoreTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_symtest_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            _store?.Dispose();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private SqliteIndexStore CreateStore()
        {
            _store?.Dispose();
            _store = new SqliteIndexStore(_dbPath);
            _store.Open(_dbPath);
            _store.RunMigrations();
            return _store;
        }

        private static byte[] StringToBytes(string text) => Encoding.UTF8.GetBytes(text);

        private static void CreateSnapshotWithDocument(
            SqliteIndexStore store, string snapshotId)
        {
            var sourceBytes = StringToBytes(
                "using System;\n" +
                "namespace TestNs {\n" +
                "    public class Foo {\n" +
                "        public void Bar() { Console.WriteLine(); }\n" +
                "    }\n" +
                "}\n");

            var lineStarts = "[0,14,33,56,107,113]";

            var manifest = new SnapshotRow
            {
                SnapshotId = snapshotId,
                WorkspaceId = "workspace:///root/proj",
                GitRoot = "/root",
                SolutionPath = "/root/proj",
                SdkVersion = "10.0.301",
                CompilerVersion = "4.12.0.0",
                CreatedAtUtc = DateTime.UtcNow,
                Documents = new List<DocumentVersion>
                {
                    new DocumentVersion(sourceBytes) { DocumentId = "doc1", FilePath = "src/Foo.cs", ContentHash = "hash1", Encoding = "utf-8", LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue, LineStarts = lineStarts },
                }
            };
            store.SaveSnapshot(manifest);
        }

        [Fact]
        public void SaveDeclarations_And_GetSymbolInfo_MetadataOnly()
        {
            var store = CreateStore();
            var snapshotId = "snap-sym-001";
            CreateSnapshotWithDocument(store, snapshotId);

            var decl = MakeDecl(
                symbolId: "T:TestNs.Foo|assembly1",
                docCommentId: "T:TestNs.Foo",
                assembly: "assembly1",
                kind: IndexedSymbolKind.Type,
                docVersionId: "hash1",
                fullS: 33, fullE: 112,
                sigS: 33, sigE: 54,
                bodyS: 54, bodyE: 112,
                nameS: 50, nameE: 53);

            store.SaveDeclarations(snapshotId, [decl]);

            var info = store.GetSymbolInfo("T:TestNs.Foo|assembly1", snapshotId);
            Assert.NotNull(info);
            Assert.Equal("T:TestNs.Foo", info!.SymbolId.DocCommentId);
            Assert.Equal("assembly1", info.SymbolId.AssemblyIdentity);
            Assert.Equal(IndexedSymbolKind.Type, info.Kind);
            Assert.Equal(1, info.DeclarationCount);
            Assert.False(info.IsPartial);
        }

        [Fact]
        public void SaveDeclarations_And_GetSymbolSource_Body()
        {
            var store = CreateStore();
            var snapshotId = "snap-sym-002";
            CreateSnapshotWithDocument(store, snapshotId);

            var decl = MakeDecl(
                symbolId: "M:TestNs.Foo.Bar|assembly1",
                docCommentId: "M:TestNs.Foo.Bar",
                assembly: "assembly1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "hash1",
                fullS: 56, fullE: 107,
                sigS: 56, sigE: 82,
                bodyS: 82, bodyE: 106,
                nameS: 76, nameE: 79);

            store.SaveDeclarations(snapshotId, [decl]);

            var body = store.GetSymbolSource("M:TestNs.Foo.Bar|assembly1", snapshotId, ViewKind.Body);
            Assert.NotNull(body);
            Assert.Equal("{ Console.WriteLine(); }", body);
        }

        [Fact]
        public void SaveDeclarations_And_GetSymbolSource_FullDeclaration()
        {
            var store = CreateStore();
            var snapshotId = "snap-sym-003";
            CreateSnapshotWithDocument(store, snapshotId);

            var decl = MakeDecl(
                symbolId: "T:TestNs.Foo|assembly1",
                docCommentId: "T:TestNs.Foo",
                assembly: "assembly1",
                kind: IndexedSymbolKind.Type,
                docVersionId: "hash1",
                fullS: 33, fullE: 112,
                sigS: 33, sigE: 54,
                bodyS: 54, bodyE: 112,
                nameS: 50, nameE: 53);

            store.SaveDeclarations(snapshotId, [decl]);

            var full = store.GetSymbolSource("T:TestNs.Foo|assembly1", snapshotId, ViewKind.Declaration);
            var expected = "    public class Foo {\n        public void Bar() { Console.WriteLine(); }\n    }";
            Assert.Equal(expected, full);
        }

        [Fact]
        public void GetSymbolInfo_SymbolNotFound_ReturnsNull()
        {
            var store = CreateStore();
            var snapshotId = "snap-sym-004";
            CreateSnapshotWithDocument(store, snapshotId);

            var info = store.GetSymbolInfo("T:Nonexistent|assembly1", snapshotId);
            Assert.Null(info);
        }

        [Fact]
        public void GetSymbolSource_NonExistentBodySpan_ReturnsNull()
        {
            var store = CreateStore();
            var snapshotId = "snap-sym-005";
            CreateSnapshotWithDocument(store, snapshotId);

            var decl = MakeDecl(
                symbolId: "M:TestNs.Foo.AbstractFoo|assembly1",
                docCommentId: "M:TestNs.Foo.AbstractFoo",
                assembly: "assembly1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "hash1",
                fullS: 33, fullE: 112,
                sigS: 33, sigE: 113,
                bodyS: null, bodyE: null,
                nameS: 50, nameE: 53);

            store.SaveDeclarations(snapshotId, [decl]);

            var body = store.GetSymbolSource("M:TestNs.Foo.AbstractFoo|assembly1", snapshotId, ViewKind.Body);
            Assert.Null(body);
        }

        [Fact]
        public void PartialType_TwoDeclarations_BothLinkedToOneSymbol()
        {
            var store = CreateStore();
            var snapshotId = "snap-sym-006";

            var source1 = StringToBytes("partial class Foo { void A() {} }\n");
            var source2 = StringToBytes("partial class Foo { void B() {} }\n");
            var lineStarts = "[0,30]";

            var manifest = new SnapshotRow
            {
                SnapshotId = snapshotId,
                WorkspaceId = "workspace:///root/proj",
                GitRoot = "/root",
                SolutionPath = "/root/proj",
                SdkVersion = "10.0.301",
                CompilerVersion = "4.12.0.0",
                CreatedAtUtc = DateTime.UtcNow,
                Documents = new List<DocumentVersion>
                {
                    new DocumentVersion(source1) { DocumentId = "doc-part1", FilePath = "src/part1.cs", ContentHash = "hash-p1", Encoding = "utf-8", LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue, LineStarts = lineStarts },
                    new DocumentVersion(source2) { DocumentId = "doc-part2", FilePath = "src/part2.cs", ContentHash = "hash-p2", Encoding = "utf-8", LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue, LineStarts = lineStarts },
                }
            };
            store.SaveSnapshot(manifest);

            var symId = new SymbolId("T:Foo", "assembly1", "TestNs.Foo");

            var decl1 = new SymbolDeclaration
            {
                SymbolId = symId,
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "hash-p1",
                FullSpan = new DeclarationSpan(0, 29),
                SignatureSpan = new DeclarationSpan(0, 15),
                BodySpan = new DeclarationSpan(15, 28),
                NameSpan = new DeclarationSpan(15, 18),
                IsPartial = true
            };

            var decl2 = new SymbolDeclaration
            {
                SymbolId = symId,
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "hash-p2",
                FullSpan = new DeclarationSpan(0, 29),
                SignatureSpan = new DeclarationSpan(0, 15),
                BodySpan = new DeclarationSpan(15, 28),
                NameSpan = new DeclarationSpan(15, 18),
                IsPartial = true
            };

            store.SaveDeclarations(snapshotId, [decl1, decl2]);

            var info = store.GetSymbolInfo(symId.Value, snapshotId);
            Assert.NotNull(info);
            Assert.Equal(2, info!.DeclarationCount);
            Assert.True(info.IsPartial);

            var body1 = store.GetSymbolSource(symId.Value, snapshotId, ViewKind.Body);
            Assert.NotNull(body1);

            var body2 = store.GetSymbolSource(symId.Value, snapshotId, ViewKind.Body);
            Assert.NotNull(body2);
        }

        [Fact]
        public void SymbolSource_RoundTripSignature()
        {
            var store = CreateStore();
            var snapshotId = "snap-sym-007";
            CreateSnapshotWithDocument(store, snapshotId);

            var decl = MakeDecl(
                symbolId: "M:TestNs.Foo.Bar|assembly1",
                docCommentId: "M:TestNs.Foo.Bar",
                assembly: "assembly1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "hash1",
                fullS: 56, fullE: 107,
                sigS: 56, sigE: 82,
                bodyS: 82, bodyE: 106,
                nameS: 76, nameE: 79);

            store.SaveDeclarations(snapshotId, [decl]);

            var sig = store.GetSymbolSource("M:TestNs.Foo.Bar|assembly1", snapshotId, ViewKind.Signature);
            Assert.Equal("        public void Bar() ", sig);
        }

        [Fact]
        public void SymbolSource_RoundTripName()
        {
            var store = CreateStore();
            var snapshotId = "snap-sym-008";
            CreateSnapshotWithDocument(store, snapshotId);

            var decl = MakeDecl(
                symbolId: "M:TestNs.Foo.Bar|assembly1",
                docCommentId: "M:TestNs.Foo.Bar",
                assembly: "assembly1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "hash1",
                fullS: 56, fullE: 107,
                sigS: 56, sigE: 82,
                bodyS: 82, bodyE: 106,
                nameS: 76, nameE: 79);

            store.SaveDeclarations(snapshotId, [decl]);

            var name = store.GetSymbolSource("M:TestNs.Foo.Bar|assembly1", snapshotId, ViewKind.Name);
            Assert.Equal("Bar", name);
        }
    }

    public class FtsSearchTests : IDisposable
    {
        private readonly string _dbPath;
        private SqliteIndexStore? _store;

        public FtsSearchTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_fts_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            _store?.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private SqliteIndexStore CreateStore()
        {
            _store?.Dispose();
            var store = new SqliteIndexStore(_dbPath);
            store.Open(_dbPath);
            store.RunMigrations();
            _store = store;
            return store;
        }

        private static byte[] StringToBytes(string text) => Encoding.UTF8.GetBytes(text);

        private static void CreateSnapshotWithContent(
            SqliteIndexStore store, string snapshotId, string relativePath, string content)
        {
            var lineStarts = "[0]";
            var sourceBytes = StringToBytes(content);

            var manifest = new SnapshotRow
            {
                SnapshotId = snapshotId,
                WorkspaceId = "workspace:///root/proj",
                GitRoot = "/root",
                SolutionPath = "/root/proj",
                SdkVersion = "10.0.301",
                CompilerVersion = "4.12.0.0",
                CreatedAtUtc = DateTime.UtcNow,
                Documents = new List<DocumentVersion>
                {
                    new DocumentVersion(sourceBytes) { DocumentId = "doc-" + relativePath, FilePath = relativePath, ContentHash = "hash-" + relativePath, Encoding = "utf-8", LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue, LineStarts = lineStarts },
                }
            };
            store.SaveSnapshot(manifest);
        }

        [Fact]
        public void BuildSearchIndex_AfterSavingSnapshot_SearchReturnsResults()
        {
            var store = CreateStore();
            var snapshotId = "snap-fts-001";

            CreateSnapshotWithContent(store, snapshotId, "src/Program.cs",
                "using System;\nclass Program { static void Main() { Console.WriteLine(\"hello\"); } }\n");

            store.BuildSearchIndex(snapshotId);

            var results = store.SearchSource("Console", snapshotId);
            Assert.NotEmpty(results);
            Assert.Contains(results, r => r.DocumentPath == "src/Program.cs");
        }

        [Fact]
        public void SearchSource_FindsContent_ReturnsSnippet()
        {
            var store = CreateStore();
            var snapshotId = "snap-fts-002";

            CreateSnapshotWithContent(store, snapshotId, "src/calc.cs",
                "class Calculator { int Add(int a, int b) => a + b; }");

            store.BuildSearchIndex(snapshotId);

            var results = store.SearchSource("Calculator", snapshotId);
            Assert.Single(results);
            Assert.Equal("src/calc.cs", results[0].DocumentPath);
            Assert.Contains("Calculator", results[0].Snippet);
        }

        [Fact]
        public void SearchSymbols_FindsSymbolByFqnFragment()
        {
            var store = CreateStore();
            var snapshotId = "snap-fts-003";
            CreateSnapshotWithContent(store, snapshotId, "src/Foo.cs",
                "namespace N { class Foo { } }");

            var decl = new SymbolDeclaration
            {
                SymbolId = new SymbolId("T:N.Foo", "asm1", "N.Foo"),
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "hash-src/Foo.cs",
                FullSpan = new DeclarationSpan(0, 10),
                SignatureSpan = new DeclarationSpan(0, 10),
                BodySpan = new DeclarationSpan(null, null),
                NameSpan = new DeclarationSpan(0, 3)
            };

            store.SaveDeclarations(snapshotId, [decl]);
            store.BuildSearchIndex(snapshotId);

            var results = store.SearchSymbols("Foo", snapshotId);
            Assert.NotEmpty(results);
            Assert.Contains(results, r => r.FullyQualifiedName == "N.Foo");
        }

        [Fact]
        public void SearchSource_EmptyQuery_ReturnsEmptyList()
        {
            var store = CreateStore();
            var snapshotId = "snap-fts-004";
            CreateSnapshotWithContent(store, snapshotId, "src/a.cs", "class A { }");
            store.BuildSearchIndex(snapshotId);

            var results = store.SearchSource("NonExistentTermXYZ", snapshotId);
            Assert.Empty(results);
        }

        [Fact]
        public void SearchSymbols_EmptyQuery_ReturnsEmptyList()
        {
            var store = CreateStore();
            var snapshotId = "snap-fts-005";
            CreateSnapshotWithContent(store, snapshotId, "src/a.cs", "class A { }");

            var decl = new SymbolDeclaration
            {
                SymbolId = new SymbolId("T:A", "asm1", "A"),
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "hash-src/a.cs",
                FullSpan = new DeclarationSpan(0, 10),
                SignatureSpan = new DeclarationSpan(0, 10),
                BodySpan = new DeclarationSpan(null, null),
                NameSpan = new DeclarationSpan(0, 1)
            };

            store.SaveDeclarations(snapshotId, [decl]);
            store.BuildSearchIndex(snapshotId);

            var results = store.SearchSymbols("NonExistentSymbol", snapshotId);
            Assert.Empty(results);
        }

        [Fact]
        public void ResolveSymbolByFqn_ExactMatch_ReturnsSymbol()
        {
            var store = CreateStore();
            var snapshotId = "snap-fts-006";
            CreateSnapshotWithContent(store, snapshotId, "src/a.cs", "class A { }");

            var decl = new SymbolDeclaration
            {
                SymbolId = new SymbolId("T:A", "asm1", "MyNs.A"),
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "hash-src/a.cs",
                FullSpan = new DeclarationSpan(0, 10),
                SignatureSpan = new DeclarationSpan(0, 10),
                BodySpan = new DeclarationSpan(null, null),
                NameSpan = new DeclarationSpan(0, 1)
            };

            store.SaveDeclarations(snapshotId, [decl]);

            var info = store.ResolveSymbolByFqn("MyNs.A", snapshotId);
            Assert.NotNull(info);
            Assert.Equal("T:A", info!.SymbolId.DocCommentId);
            Assert.Equal("MyNs.A", info.FullyQualifiedName);
        }

        [Fact]
        public void ResolveSymbolByFqn_PartialPrefixMatch_ReturnsSymbol()
        {
            var store = CreateStore();
            var snapshotId = "snap-fts-007";
            CreateSnapshotWithContent(store, snapshotId, "src/a.cs", "class A { }");

            var decl = new SymbolDeclaration
            {
                SymbolId = new SymbolId("T:A", "asm1", "MyNs.MyClass"),
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "hash-src/a.cs",
                FullSpan = new DeclarationSpan(0, 10),
                SignatureSpan = new DeclarationSpan(0, 10),
                BodySpan = new DeclarationSpan(null, null),
                NameSpan = new DeclarationSpan(0, 1)
            };

            store.SaveDeclarations(snapshotId, [decl]);

            var info = store.ResolveSymbolByFqn("MyNs.My", snapshotId);
            Assert.NotNull(info);
            Assert.Equal("MyNs.MyClass", info!.FullyQualifiedName);
        }

        [Fact]
        public void ResolveSymbolByFqn_NoMatch_ReturnsNull()
        {
            var store = CreateStore();
            var snapshotId = "snap-fts-008";
            CreateSnapshotWithContent(store, snapshotId, "src/a.cs", "class A { }");

            var info = store.ResolveSymbolByFqn("Does.Not.Exist", snapshotId);
            Assert.Null(info);
        }

        [Fact]
        public void SearchSource_SnapshotIsolation_ReturnsOnlyScopedResults()
        {
            var store = CreateStore();
            var snap1 = "snap-fts-iso-1";
            var snap2 = "snap-fts-iso-2";

            CreateSnapshotWithContent(store, snap1, "src/a.cs", "class Alpha { }");
            store.BuildSearchIndex(snap1);

            CreateSnapshotWithContent(store, snap2, "src/b.cs", "class Beta { }");
            store.BuildSearchIndex(snap2);

            var results1 = store.SearchSource("Alpha", snap1);
            Assert.NotEmpty(results1);

            var results2 = store.SearchSource("Alpha", snap2);
            Assert.Empty(results2);
        }

        [Fact]
        public void Migration005_CreatesOperationalTables()
        {
            var runner = new MigrationRunner(_dbPath);
            runner.RunMigrations();
            Assert.Equal(15, runner.GetCurrentSchemaVersion());

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();

            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='edges';";
            Assert.NotNull(cmd.ExecuteScalar());

            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='diagnostics';";
            Assert.NotNull(cmd.ExecuteScalar());

            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='annotations';";
            Assert.NotNull(cmd.ExecuteScalar());
        }

        [Fact]
        public void Migration004_CreatesFtsTables()
        {
            var runner = new MigrationRunner(_dbPath);
            runner.RunMigrations();
            Assert.Equal(15, runner.GetCurrentSchemaVersion());

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();

            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='source_fts';";
            var sourceTable = cmd.ExecuteScalar();
            Assert.NotNull(sourceTable);

            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='symbol_fts';";
            var symbolTable = cmd.ExecuteScalar();
            Assert.NotNull(symbolTable);
        }
    }

    public class A5OperationalTests : IDisposable
    {
        private readonly string _dbPath;
        private SqliteIndexStore? _store;

        public A5OperationalTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_a5_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            _store?.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private SqliteIndexStore CreateStore()
        {
            _store?.Dispose();
            var store = new SqliteIndexStore(_dbPath);
            store.Open(_dbPath);
            store.RunMigrations();
            _store = store;
            return store;
        }

        [Fact]
        public void SaveAndGetEdges_RoundTrips()
        {
            var store = CreateStore();
            var snapshotId = "snap-a5-edges-001";

            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "T:Ns.Foo|asm1", TargetSymbolId = "T:Ns.Bar|asm1", Kind = "Inherits", Provenance = "roslyn" },
                new() { SourceSymbolId = "T:Ns.Foo|asm1", TargetSymbolId = "T:Ns.IBaz|asm1", Kind = "Implements", Provenance = "roslyn" },
            };
            store.SaveEdges(snapshotId, edges);

            var loaded = store.GetEdges(snapshotId);
            Assert.Equal(2, loaded.Count);
            Assert.Equal("T:Ns.Foo|asm1", loaded[0].SourceSymbolId);
            Assert.Equal("T:Ns.Bar|asm1", loaded[0].TargetSymbolId);
            Assert.Equal("Inherits", loaded[0].Kind);

            var filtered = store.GetEdges(snapshotId, "T:Ns.Bar|asm1");
            Assert.Single(filtered);

            store.Close();
        }

        [Fact]
        public void SaveAndGetDiagnostics_RoundTrips()
        {
            var store = CreateStore();
            var snapshotId = "snap-a5-diag-001";

            var diagnostics = new List<DiagnosticRecord>
            {
                new() { ProjectName = "MyProject", DocumentPath = "src/Program.cs", Severity = "Warning", Id = "CS0219", Message = "Variable 'x' is unused", StartLine = 10, StartColumn = 5, EndLine = 10, EndColumn = 6 },
                new() { ProjectName = "MyProject", DocumentPath = "src/Utils.cs", Severity = "Error", Id = "CS0103", Message = "The name 'foo' does not exist", StartLine = 5, StartColumn = 1, EndLine = 5, EndColumn = 4 },
            };
            store.SaveDiagnostics(snapshotId, diagnostics);

            var loaded = store.GetDiagnostics(snapshotId);
            Assert.Equal(2, loaded.Count);

            var filtered = store.GetDiagnostics(snapshotId, "MyProject");
            Assert.Equal(2, filtered.Count);

            var noMatch = store.GetDiagnostics(snapshotId, "OtherProject");
            Assert.Empty(noMatch);

            store.Close();
        }

        [Fact]
        public void SaveAndGetAnnotations_RoundTrips()
        {
            var store = CreateStore();
            var snapshotId = "snap-a5-ann-001";

            var annotations = new List<AnnotationRecord>
            {
                new("T:Ns.Foo|asm1", "obsolete", "Use Bar instead"),
                new("M:Ns.Foo.Bar|asm1", "returns", "A result object"),
            };
            store.SaveAnnotations(snapshotId, annotations);

            var loaded = store.GetAnnotations(snapshotId);
            Assert.Equal(2, loaded.Count);

            var filtered = store.GetAnnotations(snapshotId, "T:Ns.Foo|asm1");
            Assert.Single(filtered);
            Assert.Equal("obsolete", filtered[0].Kind);

            store.Close();
        }

        [Fact]
        public void SaveEdges_EmptyList_DoesNotThrow()
        {
            var store = CreateStore();
            store.SaveEdges("snap-empty", []);
            Assert.Empty(store.GetEdges("snap-empty"));
            store.Close();
        }

        [Fact]
        public void SaveDiagnostics_EmptyList_DoesNotThrow()
        {
            var store = CreateStore();
            store.SaveDiagnostics("snap-empty", []);
            Assert.Empty(store.GetDiagnostics("snap-empty"));
            store.Close();
        }

        [Fact]
        public void SaveAnnotations_EmptyList_DoesNotThrow()
        {
            var store = CreateStore();
            store.SaveAnnotations("snap-empty", []);
            Assert.Empty(store.GetAnnotations("snap-empty"));
            store.Close();
        }
    }

    public class ExtractorRegistryTests : IDisposable
    {
        private readonly string _dbPath;
        private SqliteIndexStore? _store;

        public ExtractorRegistryTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_extreg_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            _store?.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private SqliteIndexStore CreateStore()
        {
            _store?.Dispose();
            var store = new SqliteIndexStore(_dbPath);
            store.Open(_dbPath);
            store.RunMigrations();
            _store = store;
            return store;
        }

        [Fact]
        public void UpsertExtractors_PopulatesTable()
        {
            var store = CreateStore();
            store.UpsertExtractors(ExtractorRegistry.All);

            // Verify the table is non-empty
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM extractors";
            var count = (long)cmd.ExecuteScalar()!;
            Assert.True(count > 0, "extractors table should have at least one row after UpsertExtractors");

            // Verify every registered extractor is present
            foreach (var (name, version, _) in ExtractorRegistry.All)
            {
                cmd.CommandText = "SELECT COUNT(*) FROM extractors WHERE name = @name AND version = @version";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@version", version);
                var rowCount = (long)cmd.ExecuteScalar()!;
                Assert.Equal(1, rowCount);
            }

            store.Close();
        }

        [Fact]
        public void UpsertExtractors_IsIdempotent()
        {
            var store = CreateStore();

            // First call
            store.UpsertExtractors(ExtractorRegistry.All);
            var countAfterFirst = GetExtractorCount();

            // Second call — should not duplicate
            store.UpsertExtractors(ExtractorRegistry.All);
            var countAfterSecond = GetExtractorCount();

            Assert.Equal(countAfterFirst, countAfterSecond);
            store.Close();

            long GetExtractorCount()
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM extractors";
                return (long)cmd.ExecuteScalar()!;
            }
        }

        [Fact]
        public void UpsertExtractors_AllVersionsMatchRegistryConstants()
        {
            // Every registered version string should match an ExtractorConstants or VersionConstants value,
            // or one of the adapter version strings. This test is the build-time guard that the registry
            // stays in sync with the code.

            // Collect all known version strings that can appear in edges.extractor_version
            var known = new HashSet<string>
            {
                // ExtractorConstants
                ExtractorConstants.DeclaresExtractor,
                ExtractorConstants.CallsExtractor,
                ExtractorConstants.ConstructsExtractor,
                ExtractorConstants.OverridesExtractor,
                ExtractorConstants.ReadsWritesExtractor,
                ExtractorConstants.ReturnsExtractor,
                ExtractorConstants.ThrowsExtractor,
                ExtractorConstants.ParameterDependenciesExtractor,
                ExtractorConstants.ReflectionExtractor,
                ExtractorConstants.StaticallyCallsExtractor,
                ExtractorConstants.PolymorphismExtractor,
                ExtractorConstants.DependencyInjectionExtractor,
                // VersionConstants
                VersionConstants.ExtractorVersion,
                // Adapter versions (hard-coded strings that appear in edge records)
                "aspnetcore-v1",
                "mediatr-v1",
                "efcore-v1",
                "serialization-v1",
                "test-v1",
            };

            var registryVersions = new HashSet<string>(ExtractorRegistry.All.Select(e => e.Version));

            foreach (var knownVersion in known)
            {
                Assert.True(registryVersions.Contains(knownVersion),
                    $"Registry is missing version '{knownVersion}'. Add it to ExtractorRegistry.All.");
            }
        }

        [Fact]
        public void EdgeExtractorVersions_AreCoveredByRegistry()
        {
            // Simulate: write edges with known extractor versions, upsert registry,
            // then verify every extractor_version in edges has a matching row in extractors.
            var store = CreateStore();
            var snapshotId = "snap-extreg-001";

            // Create edges using every known extractor version
            var edges = ExtractorRegistry.All.Select(e => new EdgeRecord
            {
                SourceSymbolId = "T:Src|asm1",
                TargetSymbolId = "T:Tgt|asm1",
                Kind = "References",
                Provenance = "compiler_proved",
                SnapshotId = snapshotId,
                ExtractorVersion = e.Version,
            }).ToList();

            // Also include an edge with "1.3.0" (VersionConstants.ExtractorVersion) to cover the Structural extractor
            edges.Add(new EdgeRecord
            {
                SourceSymbolId = "T:Src|asm1",
                TargetSymbolId = "T:Tgt2|asm1",
                Kind = "References",
                Provenance = "compiler_proved",
                SnapshotId = snapshotId,
                ExtractorVersion = VersionConstants.ExtractorVersion,
            });

            store.SaveEdges(snapshotId, edges);
            store.UpsertExtractors(ExtractorRegistry.All);

            // The acceptance query from the task:
            // SELECT DISTINCT extractor_version FROM edges
            // must be a subset of extractors.version (or name||version)
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT DISTINCT e.extractor_version
                FROM edges e
                WHERE e.extractor_version NOT IN (SELECT version FROM extractors)
                  AND e.snapshot_id = @snapshotId
            ";
            cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
            using var reader = cmd.ExecuteReader();
            var uncovered = new List<string>();
            while (reader.Read())
                uncovered.Add(reader.GetString(0));

            Assert.Empty(uncovered);
            store.Close();
        }

        [Fact]
        public void HasStaleExtractorVersions_DetectsVersionBump()
        {
            // Regression for T3 Finding 2: UpsertExtractors must prune superseded
            // (name, version) rows, otherwise old versions never leave the
            // extractors table and staleness can never be detected.
            var store = CreateStore();
            var snapshotId = "snap-extreg-bump";

            store.UpsertExtractors([("Calls", "calls-v1", "desc")]);

            store.SaveEdges(snapshotId, [new EdgeRecord
            {
                SourceSymbolId = "T:Src|asm1",
                TargetSymbolId = "T:Tgt|asm1",
                Kind = "Calls",
                Provenance = "compiler_proved",
                SnapshotId = snapshotId,
                ExtractorVersion = "calls-v1",
            }]);

            Assert.False(store.HasStaleExtractorVersions(snapshotId),
                "Edges should not be stale immediately after being written with the current version.");

            // Simulate a version bump: the extractor registry now reports calls-v2.
            store.UpsertExtractors([("Calls", "calls-v2", "desc")]);

            Assert.True(store.HasStaleExtractorVersions(snapshotId),
                "Edges written with a superseded extractor version must be detected as stale after a version bump.");

            store.Close();
        }
    }

    public class B0ExpansionTests : IDisposable
    {
        private readonly string _dbPath;
        private SqliteIndexStore? _store;

        public B0ExpansionTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_b0_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            _store?.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private SqliteIndexStore CreateStore()
        {
            _store?.Dispose();
            var store = new SqliteIndexStore(_dbPath);
            store.Open(_dbPath);
            store.RunMigrations();
            _store = store;
            return store;
        }

        [Fact]
        public void Migration006_RunTwice_IsIdempotent()
        {
            var runner = new MigrationRunner(_dbPath);

            runner.RunMigrations();
            Assert.Equal(15, runner.GetCurrentSchemaVersion());

            runner.RunMigrations();
            Assert.Equal(15, runner.GetCurrentSchemaVersion());
        }

        [Fact]
        public void SaveAndGetEdge_WithAllNewFields_RoundTrips()
        {
            var store = CreateStore();
            var snapshotId = "snap-b0-rt-001";

            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:Ns.Foo.Bar|asm1", TargetSymbolId = "M:Ns.Baz.Qux|asm1", Kind = "Calls", Provenance = "compiler_proved", SnapshotId = snapshotId, ExtractorVersion = "member-edges-v1", SourceDocumentPath = "src/Foo.cs", SourceStartLine = 42, SourceStartColumn = 13, SourceEndLine = 42, SourceEndColumn = 30 }
            };

            store.SaveEdges(snapshotId, edges);

            var loaded = store.GetEdges(snapshotId);
            var edge = Assert.Single(loaded);

            Assert.Equal("M:Ns.Foo.Bar|asm1", edge.SourceSymbolId);
            Assert.Equal("M:Ns.Baz.Qux|asm1", edge.TargetSymbolId);
            Assert.Equal("Calls", edge.Kind);
            Assert.Equal("compiler_proved", edge.Provenance);
            Assert.Equal(snapshotId, edge.SnapshotId);
            Assert.Equal("member-edges-v1", edge.ExtractorVersion);
            Assert.Equal("src/Foo.cs", edge.SourceDocumentPath);
            Assert.Equal(42, edge.SourceStartLine);
            Assert.Equal(13, edge.SourceStartColumn);
            Assert.Equal(42, edge.SourceEndLine);
            Assert.Equal(30, edge.SourceEndColumn);

            store.Close();
        }

        [Fact]
        public void SaveAndGetEdge_WithNullLocationFields_RoundTrips()
        {
            var store = CreateStore();
            var snapshotId = "snap-b0-rt-002";

            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "T:Ns.Foo|asm1", TargetSymbolId = "T:Ns.Bar|asm1", Kind = "Inherits", Provenance = "compiler_proved", SnapshotId = snapshotId, ExtractorVersion = "v1" }
            };

            store.SaveEdges(snapshotId, edges);

            var loaded = store.GetEdges(snapshotId);
            var edge = Assert.Single(loaded);

            Assert.Equal("Inherits", edge.Kind);
            Assert.Equal("compiler_proved", edge.Provenance);
            Assert.Equal(snapshotId, edge.SnapshotId);
            Assert.Equal("v1", edge.ExtractorVersion);
            Assert.Null(edge.SourceDocumentPath);
            Assert.Null(edge.SourceStartLine);
            Assert.Null(edge.SourceStartColumn);
            Assert.Null(edge.SourceEndLine);
            Assert.Null(edge.SourceEndColumn);

            store.Close();
        }

        [Fact]
        public void SaveAndGetEdge_BackwardCompatibleConstructor_StillWorks()
        {
            var store = CreateStore();
            var snapshotId = "snap-b0-bc-001";

            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "T:Ns.Foo|asm1", TargetSymbolId = "T:Ns.Bar|asm1", Kind = "Inherits", Provenance = "roslyn" },
                new() { SourceSymbolId = "T:Ns.Foo|asm1", TargetSymbolId = "T:Ns.IBaz|asm1", Kind = "Implements" },
            };

            store.SaveEdges(snapshotId, edges);

            var loaded = store.GetEdges(snapshotId);
            Assert.Equal(2, loaded.Count);

            Assert.Equal("T:Ns.Foo|asm1", loaded[0].SourceSymbolId);
            Assert.Equal("T:Ns.Bar|asm1", loaded[0].TargetSymbolId);
            Assert.Equal("Inherits", loaded[0].Kind);
            Assert.Equal("roslyn", loaded[0].Provenance);

            Assert.Equal(snapshotId, loaded[0].SnapshotId);
            Assert.Equal("", loaded[0].ExtractorVersion);
            Assert.Null(loaded[0].SourceDocumentPath);

            Assert.Equal("Implements", loaded[1].Kind);
            Assert.Equal("", loaded[1].Provenance);

            var filtered = store.GetEdges(snapshotId, "T:Ns.Bar|asm1");
            Assert.Single(filtered);

            store.Close();
        }

        [Fact]
        public void GetEdgesByKind_FiltersCorrectly()
        {
            var store = CreateStore();
            var snapshotId = "snap-b0-kind-001";

            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "T:A|asm1", TargetSymbolId = "T:Base|asm1", Kind = "Inherits", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "T:A|asm1", TargetSymbolId = "T:IFoo|asm1", Kind = "Implements", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:A.Foo|asm1", TargetSymbolId = "M:B.Bar|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            store.SaveEdges(snapshotId, edges);

            var inherits = store.GetEdgesByKind(snapshotId, "Inherits");
            Assert.Single(inherits);

            var calls = store.GetEdgesByKind(snapshotId, "Calls");
            Assert.Single(calls);

            var nonExistent = store.GetEdgesByKind(snapshotId, "RoutesTo");
            Assert.Empty(nonExistent);

            store.Close();
        }

        [Fact]
        public void GetIncomingEdges_ReturnsEdgesTargetingSymbol()
        {
            var store = CreateStore();
            var snapshotId = "snap-b0-in-001";

            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A.Foo|asm1", TargetSymbolId = "M:B.Bar|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:C.Qux|asm1", TargetSymbolId = "M:B.Bar|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:B.Bar|asm1", TargetSymbolId = "M:D.Other|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            store.SaveEdges(snapshotId, edges);

            var incoming = store.GetIncomingEdges(snapshotId, "M:B.Bar|asm1");
            Assert.Equal(2, incoming.Count);
            Assert.All(incoming, e => Assert.Equal("M:B.Bar|asm1", e.TargetSymbolId));

            store.Close();
        }

        [Fact]
        public void GetOutgoingEdges_ReturnsEdgesFromSymbol()
        {
            var store = CreateStore();
            var snapshotId = "snap-b0-out-001";

            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A.Foo|asm1", TargetSymbolId = "M:B.Bar|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:A.Foo|asm1", TargetSymbolId = "M:C.Qux|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:B.Bar|asm1", TargetSymbolId = "M:D.Other|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            store.SaveEdges(snapshotId, edges);

            var outgoing = store.GetOutgoingEdges(snapshotId, "M:A.Foo|asm1");
            Assert.Equal(2, outgoing.Count);
            Assert.All(outgoing, e => Assert.Equal("M:A.Foo|asm1", e.SourceSymbolId));

            store.Close();
        }
    }

    public class B1MemberEdgeExtractorTests
    {
        private static Compilation CreateCompilation(string source, string path = "test.cs")
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source, path: path);
            return CSharpCompilation.Create(
                "TestAssembly",
                [syntaxTree],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        }

        private static IReadOnlyDictionary<DocumentId, DocumentVersionId> CreateDocVersions(string path)
        {
            return new Dictionary<DocumentId, DocumentVersionId>
            {
                { new DocumentId(path), DocumentVersionId.Compute("test-content") }
            };
        }

        [Fact]
        public void Declares_ClassWithOneMethod_EmitsDeclaresEdge()
        {
            var source = @"
class Foo {
    void Bar() {}
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-decl", "/");

            var edges = extractor.ExtractAll();

            var declares = edges.Where(e => e.Kind == "Declares").ToList();
            Assert.NotEmpty(declares);
            Assert.Contains(declares, e =>
                e.SourceSymbolId.Contains("Foo") &&
                e.TargetSymbolId.Contains("Bar"));
        }

        [Fact]
        public void Calls_MethodACallsMethodB_EmitsCallsEdge()
        {
            var source = @"
class Foo {
    void A() { B(); }
    void B() {}
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-calls", "/");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Calls" &&
                e.SourceSymbolId.Contains('A') &&
                e.TargetSymbolId.Contains('B'));
        }

        [Fact]
        public void Constructs_MethodNewFoo_EmitsConstructsEdge()
        {
            var source = @"
class Foo {
    public Foo() {}
}
class Bar {
    void M() { var x = new Foo(); }
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-ctor", "/");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Constructs" &&
                e.SourceSymbolId.Contains('M') &&
                e.TargetSymbolId.Contains("Foo") &&
                e.TargetSymbolId.Contains("#ctor"));
        }

        [Fact]
        public void Overrides_DerivedOverridesVirtual_EmitsOverridesEdge()
        {
            var source = @"
class Base {
    public virtual void M() {}
}
class Derived : Base {
    public override void M() {}
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-override", "/");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Overrides" &&
                e.SourceSymbolId.Contains("Derived") &&
                e.TargetSymbolId.Contains("Base"));
        }

        [Fact]
        public void ReadsWrites_MethodReadsAndWritesField_EmitsBothEdges()
        {
            var source = @"
class Foo {
    int _field;
    void M() { _field = 1; int x = _field; }
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-rw", "/");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Writes" &&
                e.SourceSymbolId.Contains('M') &&
                e.TargetSymbolId.Contains("_field"));

            Assert.Contains(edges, e =>
                e.Kind == "Reads" &&
                e.SourceSymbolId.Contains('M') &&
                e.TargetSymbolId.Contains("_field"));
        }

        [Fact]
        public void Returns_MethodWithNonVoidReturn_EmitsReturnsEdge()
        {
            var source = @"
class Foo {
    string M() { return ""; }
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-ret", "/");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Returns" &&
                e.SourceSymbolId.Contains('M') &&
                e.TargetSymbolId.Contains("String"));
        }

        [Fact]
        public void Throws_MethodThrowsException_EmitsThrowsEdge()
        {
            var source = @"
class Foo {
    void M() { throw new System.InvalidOperationException(); }
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-throw", "/");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Throws" &&
                e.SourceSymbolId.Contains('M') &&
                e.TargetSymbolId.Contains("InvalidOperationException"));
        }
    }

    public class B2PolymorphismExtractorTests
    {
        private static Compilation CreateCompilation(string source, string path = "test.cs")
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source, path: path);
            return CSharpCompilation.Create(
                "TestAssembly",
                [syntaxTree],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        }

        [Fact]
        public void InterfaceDispatch_ClassImplementsInterface_EmitsMayDispatchTo()
        {
            var source = @"
interface IFoo {
    void Bar();
}
class Foo : IFoo {
    public void Bar() {}
}";
            var compilation = CreateCompilation(source);
            var extractor = new PolymorphismExtractor(compilation, "snap-poly-iface", "/");

            var edges = extractor.ExtractAll();

            var dispatchEdge = Assert.Single(edges, e => e.Kind == "MayDispatchTo");
            Assert.Equal("compiler_proved", dispatchEdge.Provenance);
            Assert.Contains("IFoo", dispatchEdge.SourceSymbolId);
            Assert.Contains("Foo", dispatchEdge.TargetSymbolId);
            Assert.Contains("Bar", dispatchEdge.TargetSymbolId);
        }

        [Fact]
        public void VirtualOverride_DerivedOverridesVirtual_EmitsMayDispatchTo()
        {
            var source = @"
class Base {
    public virtual void M() {}
}
class Derived : Base {
    public override void M() {}
}";
            var compilation = CreateCompilation(source);
            var extractor = new PolymorphismExtractor(compilation, "snap-poly-virt", "/");

            var edges = extractor.ExtractAll();

            var dispatchEdge = Assert.Single(edges, e => e.Kind == "MayDispatchTo");
            Assert.Equal("compiler_proved", dispatchEdge.Provenance);
            Assert.Contains("Base", dispatchEdge.SourceSymbolId);
            Assert.Contains("Derived", dispatchEdge.TargetSymbolId);
            Assert.Contains("M", dispatchEdge.TargetSymbolId);
        }
    }

    public class B3SemanticChangesTests : IDisposable
    {
        private readonly string _dbPath;
        private SqliteIndexStore? _store;

        public B3SemanticChangesTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_b3test_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            _store?.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private static byte[] StringToBytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

        private static void CreateSnapshotWithDocument(
            SqliteIndexStore store, string snapshotId)
        {
            var sourceBytes = StringToBytes(
                "using System;\n" +
                "namespace TestNs {\n" +
                "    public class Foo {\n" +
                "        public void Bar() { Console.WriteLine(); }\n" +
                "    }\n" +
                "}\n");

            var lineStarts = "[0,14,33,56,107,113]";

            var manifest = new SnapshotRow
            {
                SnapshotId = snapshotId,
                WorkspaceId = "workspace:///root/proj",
                GitRoot = "/root",
                SolutionPath = "/root/proj",
                SdkVersion = "10.0.301",
                CompilerVersion = "4.12.0.0",
                CreatedAtUtc = DateTime.UtcNow,
                Documents = new List<DocumentVersion>
                {
                    new DocumentVersion(sourceBytes) { DocumentId = "doc-" + snapshotId, FilePath = "src/Foo.cs", ContentHash = "hash1", Encoding = "utf-8", LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue, LineStarts = lineStarts },
                }
            };
            store.SaveSnapshot(manifest);
        }

        [Fact]
        public void Migration_007_IsIdempotent()
        {
            var runner = new MigrationRunner(_dbPath);
            runner.RunMigrations();
            Assert.Equal(15, runner.GetCurrentSchemaVersion());

            runner.RunMigrations();
            Assert.Equal(15, runner.GetCurrentSchemaVersion());

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();

            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='semantic_changes';";
            var tableExists = cmd.ExecuteScalar();
            Assert.NotNull(tableExists);

            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name='idx_semantic_changes_to_snapshot';";
            var indexExists = cmd.ExecuteScalar();
            Assert.NotNull(indexExists);

            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name='idx_semantic_changes_from_to';";
            var indexExists2 = cmd.ExecuteScalar();
            Assert.NotNull(indexExists2);
        }

        [Fact]
        public void SaveAndGetSemanticChanges_RoundTrip()
        {
            var store = new SqliteIndexStore(_dbPath);
            _store = store;
            store.Open(_dbPath);
            store.RunMigrations();

            var fromSnapshotId = "snap-b3-001";
            var toSnapshotId = "snap-b3-002";

            var changes = new List<SemanticChange>
            {
                new() {
                    ChangeId = "change-1",
                    FromSnapshotId = fromSnapshotId,
                    ToSnapshotId = toSnapshotId,
                    ChangeType = ChangeType.SymbolAdded,
                    SymbolId = "M:Ns.Foo|asm1",
                    DetailJson = "{\"symbol_id\": \"M:Ns.Foo|asm1\"}",
                    CreatedAtUtc = DateTime.UtcNow
                },
                new() {
                    ChangeId = "change-2",
                    FromSnapshotId = fromSnapshotId,
                    ToSnapshotId = toSnapshotId,
                    ChangeType = ChangeType.SymbolRemoved,
                    SymbolId = "M:Ns.Bar|asm1",
                    DetailJson = "{\"symbol_id\": \"M:Ns.Bar|asm1\"}",
                    CreatedAtUtc = DateTime.UtcNow
                },
            };

            store.SaveSemanticChanges(fromSnapshotId, toSnapshotId, changes);

            var loaded = store.GetSemanticChanges(fromSnapshotId, toSnapshotId);
            Assert.Equal(2, loaded.Count);

            var change1 = loaded[0];
            Assert.Equal("change-1", change1.ChangeId);
            Assert.Equal(fromSnapshotId, change1.FromSnapshotId);
            Assert.Equal(toSnapshotId, change1.ToSnapshotId);
            Assert.Equal(ChangeType.SymbolAdded, change1.ChangeType);
            Assert.Equal("M:Ns.Foo|asm1", change1.SymbolId);
            Assert.Equal(
                "{\"symbol_id\": \"M:Ns.Foo|asm1\"}",
                change1.DetailJson);

            var change2 = loaded[1];
            Assert.Equal("change-2", change2.ChangeId);
            Assert.Equal(fromSnapshotId, change2.FromSnapshotId);
            Assert.Equal(toSnapshotId, change2.ToSnapshotId);
            Assert.Equal(ChangeType.SymbolRemoved, change2.ChangeType);
            Assert.Equal("M:Ns.Bar|asm1", change2.SymbolId);
            Assert.Equal(
                "{\"symbol_id\": \"M:Ns.Bar|asm1\"}",
                change2.DetailJson);
        }

        [Fact]
        public void GetSemanticChanges_EmptyList_ReturnsEmpty()
        {
            var store = new SqliteIndexStore(_dbPath);
            _store = store;
            store.Open(_dbPath);
            store.RunMigrations();

            var fromSnapshotId = "snap-b3-003";
            var toSnapshotId = "snap-b3-004";

            var loaded = store.GetSemanticChanges(fromSnapshotId, toSnapshotId);
            Assert.Empty(loaded);
        }

        [Fact]
        public void SemanticDiffer_SymbolAddedAndRemoved()
        {
            var store = new SqliteIndexStore(_dbPath);
            _store = store;
            store.Open(_dbPath);
            store.RunMigrations();

            var fromSnapshotId = "snap-b3-005";
            var toSnapshotId = "snap-b3-006";
            CreateSnapshotWithDocument(store, fromSnapshotId);
            CreateSnapshotWithDocument(store, toSnapshotId);

            var fromSymbols = new List<string>
            {
                "M:Ns.Foo|asm1",
                "M:Ns.Bar|asm1",
            };
            var toSymbols = new List<string>
            {
                "M:Ns.Bar|asm1",
                "M:Ns.Baz|asm1",
            };

            foreach (var symbolId in fromSymbols)
            {
                var decl = MakeDecl(
                    symbolId: symbolId,
                    docCommentId: "M:Ns.Foo",
                    assembly: "asm1",
                    kind: IndexedSymbolKind.Method,
                    docVersionId: "hash1",
                    fullS: 0, fullE: 10,
                    sigS: 0, sigE: 5,
                    bodyS: 6, bodyE: 10,
                    nameS: 0, nameE: 5);
                store.SaveDeclarations(fromSnapshotId, [decl]);
            }

            foreach (var symbolId in toSymbols)
            {
                var decl = MakeDecl(
                    symbolId: symbolId,
                    docCommentId: "M:Ns.Baz",
                    assembly: "asm1",
                    kind: IndexedSymbolKind.Method,
                    docVersionId: "hash1",
                    fullS: 0, fullE: 10,
                    sigS: 0, sigE: 5,
                    bodyS: 6, bodyE: 10,
                    nameS: 0, nameE: 5);
                store.SaveDeclarations(toSnapshotId, [decl]);
            }

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            var symbolAdded = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SymbolAdded);
            Assert.NotNull(symbolAdded);
            Assert.Equal("M:Ns.Baz|asm1", symbolAdded.SymbolId);

            var symbolRemoved = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SymbolRemoved);
            Assert.NotNull(symbolRemoved);
            Assert.Equal("M:Ns.Foo|asm1", symbolRemoved.SymbolId);
        }

        [Fact]
        public void SemanticDiffer_EdgeAddedAndRemoved()
        {
            var store = new SqliteIndexStore(_dbPath);
            _store = store;
            store.Open(_dbPath);
            store.RunMigrations();

            var fromSnapshotId = "snap-b3-009";
            var toSnapshotId = "snap-b3-010";

            var fromEdges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:Ns.Foo|asm1",
                    TargetSymbolId = "M:Ns.Bar|asm1",
                    Kind = "Calls",
                    Provenance = "compiler_proved",
                },
            };
            var toEdges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:Ns.Bar|asm1",
                    TargetSymbolId = "M:Ns.Baz|asm1",
                    Kind = "Calls",
                    Provenance = "compiler_proved",
                },
            };

            store.SaveEdges(fromSnapshotId, fromEdges);
            store.SaveEdges(toSnapshotId, toEdges);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            var edgeAdded = changes.FirstOrDefault(c => c.ChangeType == ChangeType.EdgeAdded);
            Assert.NotNull(edgeAdded);
            Assert.Equal("M:Ns.Bar|asm1", edgeAdded.SymbolId);
            Assert.Contains("\"target\":\"M:Ns.Baz|asm1\"", edgeAdded.DetailJson!);

            var edgeRemoved = changes.FirstOrDefault(c => c.ChangeType == ChangeType.EdgeRemoved);
            Assert.NotNull(edgeRemoved);
            Assert.Equal("M:Ns.Foo|asm1", edgeRemoved.SymbolId);
            Assert.Contains("\"target\":\"M:Ns.Bar|asm1\"", edgeRemoved.DetailJson!);
        }

        [Fact]
        public void SemanticDiffer_SignatureChangedViaMetadata()
        {
            var store = new SqliteIndexStore(_dbPath);
            _store = store;
            store.Open(_dbPath);
            store.RunMigrations();

            var fromSnapshotId = "snap-b3-011";
            var toSnapshotId = "snap-b3-012";
            CreateSnapshotWithDocument(store, fromSnapshotId);
            CreateSnapshotWithDocument(store, toSnapshotId);

            var symbolId = "M:Ns.Foo|asm1";

            var fromDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"signature\": \"void Foo()\", \"return_type\": \"void\"}");

            var toDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"signature\": \"int Foo()\", \"return_type\": \"int\"}");

            store.SaveDeclarations(fromSnapshotId, [fromDecl]);
            store.SaveDeclarations(toSnapshotId, [toDecl]);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            var signatureChanged = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SignatureChanged);
            Assert.NotNull(signatureChanged);
            Assert.Equal(symbolId, signatureChanged.SymbolId);
        }

        [Fact]
        public void SemanticDiffer_SymbolRenamed()
        {
            var store = new SqliteIndexStore(_dbPath);
            _store = store;
            store.Open(_dbPath);
            store.RunMigrations();

            var fromSnapshotId = "snap-b3-013";
            var toSnapshotId = "snap-b3-014";
            CreateSnapshotWithDocument(store, fromSnapshotId);
            CreateSnapshotWithDocument(store, toSnapshotId);

            var symbolId = "M:Ns.Foo|asm1";

            var fromDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                fqn: "Ns.OldName");

            var toDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                fqn: "Ns.NewName");

            store.SaveDeclarations(fromSnapshotId, [fromDecl]);
            store.SaveDeclarations(toSnapshotId, [toDecl]);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            var symbolRenamed = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SymbolRenamed);
            Assert.NotNull(symbolRenamed);
            Assert.Equal(symbolId, symbolRenamed.SymbolId);
            Assert.Contains("before", symbolRenamed.DetailJson!);
            Assert.Contains("after", symbolRenamed.DetailJson!);
        }

        [Fact]
        public void SemanticDiffer_EmptyDiff()
        {
            var store = new SqliteIndexStore(_dbPath);
            _store = store;
            store.Open(_dbPath);
            store.RunMigrations();

            var fromSnapshotId = "snap-b3-015";
            var toSnapshotId = "snap-b3-016";
            CreateSnapshotWithDocument(store, fromSnapshotId);
            CreateSnapshotWithDocument(store, toSnapshotId);

            var symbolId = "M:Ns.Foo|asm1";

            var fromDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5);

            var toDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5);

            store.SaveDeclarations(fromSnapshotId, [fromDecl]);
            store.SaveDeclarations(toSnapshotId, [toDecl]);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            Assert.Empty(changes);
        }
    }

    public class B4GeneratedCodeTests : IDisposable
    {
        private readonly string _dbPath;
        private SqliteIndexStore? _store;

        public B4GeneratedCodeTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_b4_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            _store?.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private SqliteIndexStore CreateStore()
        {
            _store?.Dispose();
            var store = new SqliteIndexStore(_dbPath);
            store.Open(_dbPath);
            store.RunMigrations();
            _store = store;
            return store;
        }

        private static byte[] StringToBytes(string text) => Encoding.UTF8.GetBytes(text);

        private static void CreateSnapshotWithDocument(
            SqliteIndexStore store, string snapshotId, string relativePath, string content)
        {
            var lineStarts = "[0]";
            var sourceBytes = StringToBytes(content);

            var manifest = new SnapshotRow
            {
                SnapshotId = snapshotId,
                WorkspaceId = "workspace:///root/proj",
                GitRoot = "/root",
                SolutionPath = "/root/proj",
                SdkVersion = "10.0.301",
                CompilerVersion = "4.12.0.0",
                CreatedAtUtc = DateTime.UtcNow,
                Documents = new List<DocumentVersion>
                {
                    new DocumentVersion(sourceBytes) { DocumentId = "doc-" + relativePath, FilePath = relativePath, ContentHash = "hash1", Encoding = "utf-8", LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue, LineStarts = lineStarts },
                }
            };
            store.SaveSnapshot(manifest);
        }

        [Fact]
        public void Migration008_RunTwice_IsIdempotent()
        {
            var runner = new MigrationRunner(_dbPath);

            runner.RunMigrations();
            Assert.Equal(15, runner.GetCurrentSchemaVersion());

            runner.RunMigrations();
            Assert.Equal(15, runner.GetCurrentSchemaVersion());

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();

            cmd.CommandText = "PRAGMA table_info(declarations);";
            using var reader = cmd.ExecuteReader();
            bool hasIsGenerated = false, hasGeneratorIdentity = false;
            while (reader.Read())
            {
                var colName = reader.GetString(1);
                if (colName == "is_generated") hasIsGenerated = true;
                if (colName == "generator_identity") hasGeneratorIdentity = true;
            }
            Assert.True(hasIsGenerated, "is_generated column should exist after migration 008");
            Assert.True(hasGeneratorIdentity, "generator_identity column should exist after migration 008");
        }

        [Fact]
        public void SaveDeclaration_WithIsGenerated_RoundTrips()
        {
            var store = CreateStore();
            var snapshotId = "snap-b4-rt-gen-001";
            CreateSnapshotWithDocument(store, snapshotId, "src/Generated.cs",
                "// <auto-generated>\nclass GeneratedClass { }");

            var decl = new SymbolDeclaration
            {
                SymbolId = new SymbolId("T:GeneratedClass", "asm1", "GeneratedClass"),
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "hash1",
                FullSpan = new DeclarationSpan(0, 44),
                SignatureSpan = new DeclarationSpan(0, 26),
                BodySpan = new DeclarationSpan(26, 43),
                NameSpan = new DeclarationSpan(26, 40),
                IsGenerated = true,
                GeneratorIdentity = "SG/FooGenerator"
            };

            store.SaveDeclarations(snapshotId, [decl]);

            var info = store.GetSymbolInfo("T:GeneratedClass|asm1", snapshotId);
            Assert.NotNull(info);
            Assert.Equal("T:GeneratedClass", info!.SymbolId.DocCommentId);

            var sourceWithout = store.GetSymbolSource("T:GeneratedClass|asm1", snapshotId, ViewKind.Declaration);
            Assert.Null(sourceWithout);

            var sourceWith = store.GetSymbolSource("T:GeneratedClass|asm1", snapshotId, ViewKind.Declaration, includeGenerated: true);
            Assert.NotNull(sourceWith);
            Assert.Contains("GeneratedClass", sourceWith!);
        }

        [Fact]
        public void GetSymbolSource_ExcludesGeneratedByDefault()
        {
            var store = CreateStore();
            var snapshotId = "snap-b4-excl-001";
            CreateSnapshotWithDocument(store, snapshotId, "src/Gen.cs",
                "// <auto-generated>\nclass Gen { void Foo() { } }");

            var decl = new SymbolDeclaration
            {
                SymbolId = new SymbolId("T:Gen", "asm1", "Gen"),
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "hash1",
                FullSpan = new DeclarationSpan(0, 40),
                SignatureSpan = new DeclarationSpan(0, 20),
                BodySpan = new DeclarationSpan(21, 39),
                NameSpan = new DeclarationSpan(18, 21),
                IsGenerated = true,
                GeneratorIdentity = "auto-generated-header"
            };

            store.SaveDeclarations(snapshotId, [decl]);

            var without = store.GetSymbolSource("T:Gen|asm1", snapshotId, ViewKind.Declaration);
            Assert.Null(without);

            var with = store.GetSymbolSource("T:Gen|asm1", snapshotId, ViewKind.Declaration, includeGenerated: true);
            Assert.NotNull(with);
        }

        [Fact]
        public void Search_ExcludesGeneratedByDefault()
        {
            var store = CreateStore();
            var snapshotId = "snap-b4-search-001";

            CreateSnapshotWithDocument(store, snapshotId, "src/Normal.cs",
                "class NormalClass { }");

            var normalDecl = new SymbolDeclaration
            {
                SymbolId = new SymbolId("T:NormalClass", "asm1", "NormalClass"),
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "hash1",
                FullSpan = new DeclarationSpan(0, 20),
                SignatureSpan = new DeclarationSpan(0, 15),
                BodySpan = new DeclarationSpan(16, 19),
                NameSpan = new DeclarationSpan(7, 18),
                IsGenerated = false
            };

            var generatedDecl = new SymbolDeclaration
            {
                SymbolId = new SymbolId("T:GenClass", "asm1", "GenClass"),
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "hash1",
                FullSpan = new DeclarationSpan(0, 20),
                SignatureSpan = new DeclarationSpan(0, 15),
                BodySpan = new DeclarationSpan(16, 19),
                NameSpan = new DeclarationSpan(7, 18),
                IsGenerated = true,
                GeneratorIdentity = "test-gen"
            };

            store.SaveDeclarations(snapshotId, [normalDecl, generatedDecl]);
            store.BuildSearchIndex(snapshotId);

            var normalResults = store.SearchSymbols("NormalClass", snapshotId);
            Assert.NotEmpty(normalResults);

            var withoutGen = store.SearchSymbols("GenClass", snapshotId);
            Assert.Empty(withoutGen);

            var withGen = store.SearchSymbols("GenClass", snapshotId, includeGenerated: true);
            Assert.NotEmpty(withGen);
        }

        [Fact]
        public void CrossGeneratedProvenanceMarker_AppendedForGeneratedEdges()
        {

            var compilation = CreateCompilation("class A { string M() => \"\"; }");
            var docVersions = CreateDocVersions("test.cs");

            var generatedDocs = new HashSet<DocumentId> { new("test.cs") };
            var extractor = new MemberEdgeExtractor(compilation, docVersions, generatedDocs, "snap-b4-provenance", "/");

            var edges = extractor.ExtractAll();

            var returns = edges.Where(e => e.Kind == "Returns").ToList();
            Assert.NotEmpty(returns);
            foreach (var edge in returns)
            {
                Assert.Equal(Provenance.CompilerProved, edge.Provenance);
                Assert.True(edge.IsCrossGenerated);
            }
        }

        private static Compilation CreateCompilation(string source, string path = "test.cs")
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source, path: path);
            return CSharpCompilation.Create(
                "TestAssembly",
                [syntaxTree],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        }

        private static IReadOnlyDictionary<DocumentId, DocumentVersionId> CreateDocVersions(string path)
        {
            return new Dictionary<DocumentId, DocumentVersionId>
            {
                { new DocumentId(path), DocumentVersionId.Compute("test-content") }
            };
        }
    }

    public class B5AdapterTests
    {
        private static Compilation CreateCompilation(string source, string path = "test.cs")
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source, path: path);
            return CSharpCompilation.Create(
                "TestAssembly",
                [syntaxTree],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        }

        private static Compilation CreateCompilationWithReferences(string source, string[] refAssemblies, string path = "test.cs")
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source, path: path);
            var references = new List<MetadataReference>
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.ComponentModel.INotifyPropertyChanged).Assembly.Location),
            };
            foreach (var asm in refAssemblies)
            {
                try { references.Add(MetadataReference.CreateFromFile(asm)); }
                catch { }
            }
            return CSharpCompilation.Create(
                "TestAssembly",
                [syntaxTree],
                references);
        }

        private static readonly string MediatRStubs = @"
namespace MediatR {
    public interface IRequest<TResponse> { }
    public interface IRequestHandler<TRequest, TResponse> where TRequest : IRequest<TResponse> { }
    public interface INotification { }
    public interface INotificationHandler<TNotification> where TNotification : INotification { }
}";

        private static readonly string AspNetCoreMvcStubs = @"
namespace Microsoft.AspNetCore.Mvc {
    public class ControllerBase {
        public IActionResult Ok() => null!;
        public IActionResult Ok(object value) => null!;
    }
    public class RouteAttribute : System.Attribute {
        public RouteAttribute(string template) { }
    }
    public class HttpGetAttribute : System.Attribute {
        public HttpGetAttribute(string template) { }
    }
    public class HttpPostAttribute : System.Attribute {
        public HttpPostAttribute() { }
    }
    public class FromBodyAttribute : System.Attribute { }
    public interface IActionResult { }
}";

        private static readonly string DependencyInjectionStubs = @"
namespace Microsoft.Extensions.DependencyInjection {
    public interface IServiceCollection { }
    public static class ServiceCollectionServiceExtensions {
        public static IServiceCollection AddScoped<TService, TImplementation>(this IServiceCollection services) where TImplementation : TService => services;
        public static IServiceCollection AddTransient<TService, TImplementation>(this IServiceCollection services) where TImplementation : TService => services;
        public static IServiceCollection AddSingleton<TService, TImplementation>(this IServiceCollection services) where TImplementation : TService => services;
    }
}";

        private static readonly string EfCoreStubs = @"
namespace Microsoft.EntityFrameworkCore {
    public class DbContext { }
    public class DbSet<TEntity> where TEntity : class { }
}";

        private static readonly string XunitStubs = @"
namespace Xunit {
    public class FactAttribute : System.Attribute { }
}";

        private static Compilation CreateCompilationWithStubs(string source, string stubs, string assemblyName = "TestAssembly")
        {
            var testTree = CSharpSyntaxTree.ParseText(source, path: "test.cs");
            var stubsTree = CSharpSyntaxTree.ParseText(stubs, path: "stubs.cs");

            return CSharpCompilation.Create(
                assemblyName,
                [stubsTree, testTree],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        }


        private static EdgeLocationResolver CreateTestLocationResolver()
        {
            return new EdgeLocationResolver(
                new Dictionary<DocumentId, DocumentVersionId>(),
                new HashSet<DocumentId>(),
                ".");
        }

        private static MetadataReference EmitStubAssembly(string assemblyName, string stubSource)
        {
            var stubTree = CSharpSyntaxTree.ParseText(stubSource, path: $"{assemblyName}.cs");
            var compilation = CSharpCompilation.Create(
                assemblyName,
                [stubTree],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using var ms = new System.IO.MemoryStream();
            var emitResult = compilation.Emit(ms);
            if (!emitResult.Success)
                throw new InvalidOperationException(
                    $"{assemblyName} stub assembly emission failed: " +
                    string.Join("; ", emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            ms.Position = 0;
            return MetadataReference.CreateFromStream(ms);
        }

        private static Compilation CreateCompilationWithMediatR(string source)
        {
            var testTree = CSharpSyntaxTree.ParseText(source, path: "test.cs");
            var mediatrRef = EmitStubAssembly("MediatR", MediatRStubs);

            return CSharpCompilation.Create(
                "TestAssembly",
                [testTree],
                [
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    mediatrRef,
                ]);
        }

        private static Compilation CreateCompilationWithEfCore(string source)
        {
            var testTree = CSharpSyntaxTree.ParseText(source, path: "test.cs");
            var efCoreRef = EmitStubAssembly("Microsoft.EntityFrameworkCore", EfCoreStubs);

            return CSharpCompilation.Create(
                "TestAssembly",
                [testTree],
                [
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    efCoreRef,
                ]);
        }

        [Fact]
        public void AspNetCore_RouteAttribute_EmitsRoutesToEdge()
        {
            var source = @"
using Microsoft.AspNetCore.Mvc;

[Route(""api/users"")]
public class UsersController : ControllerBase
{
    [HttpGet(""{id}"")]
    public IActionResult GetUser(int id) => Ok();
}
";

            var compilation = CreateCompilationWithStubs(source, AspNetCoreMvcStubs);
            var adapter = new AspNetCoreAdapter();
            var edges = adapter.Extract(compilation, "snap-b5-aspnet-001", CreateTestLocationResolver());

            Assert.NotEmpty(edges);
            Assert.Contains(edges, e => e.Kind == "RoutesTo");

        }

        [Fact]
        public void AspNetCore_HttpPostAttribute_EmitsRoutesToEdge()
        {
            var source = @"
using Microsoft.AspNetCore.Mvc;

[Route(""api/orders"")]
public class OrdersController : ControllerBase
{
    [HttpPost]
    public IActionResult CreateOrder([FromBody] object order) => Ok();
}
";
            var compilation = CreateCompilationWithStubs(source, AspNetCoreMvcStubs);
            var adapter = new AspNetCoreAdapter();
            var edges = adapter.Extract(compilation, "snap-b5-aspnet-003", CreateTestLocationResolver());

            Assert.NotEmpty(edges);
            Assert.Contains(edges, e => e.Kind == "RoutesTo");

        }

        [Fact]
        public void AspNetCore_NoController_EmitsZeroEdges()
        {
            var source = @"
public class PlainClass
{
    public void DoSomething() { }
}
";
            var compilation = CreateCompilation(source);
            var adapter = new AspNetCoreAdapter();
            var edges = adapter.Extract(compilation, "snap-b5-aspnet-002", CreateTestLocationResolver());

            Assert.Empty(edges);
        }

        [Fact]
        public void DI_AddScoped_EmitsRegistersEdge()
        {
            var source = @"
using Microsoft.Extensions.DependencyInjection;

public interface IService { }
public class Service : IService { }

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<IService, Service>();
    }
}
";
            var compilation = CreateCompilationWithStubs(source, DependencyInjectionStubs);
            var adapter = new DependencyInjectionAdapter();
            var edges = adapter.Extract(compilation, "snap-b5-di-001", CreateTestLocationResolver());

            Assert.NotEmpty(edges);
            Assert.Contains(edges, e => e.Kind == "Registers" && e.TargetSymbolId.Contains("Service"));

        }

        [Fact]
        public void DI_AddTransient_EmitsRegistersEdge()
        {
            var source = @"
using Microsoft.Extensions.DependencyInjection;

public interface IService { }
public class Service : IService { }

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddTransient<IService, Service>();
    }
}
";
            var compilation = CreateCompilationWithStubs(source, DependencyInjectionStubs);
            var adapter = new DependencyInjectionAdapter();
            var edges = adapter.Extract(compilation, "snap-b5-di-003", CreateTestLocationResolver());

            Assert.NotEmpty(edges);
            Assert.Contains(edges, e => e.Kind == "Registers" && e.TargetSymbolId.Contains("Service"));
        }

        [Fact]
        public void DI_AddSingleton_EmitsRegistersEdge()
        {
            var source = @"
using Microsoft.Extensions.DependencyInjection;

public interface IService { }
public class Service : IService { }

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IService, Service>();
    }
}
";
            var compilation = CreateCompilationWithStubs(source, DependencyInjectionStubs);
            var adapter = new DependencyInjectionAdapter();
            var edges = adapter.Extract(compilation, "snap-b5-di-004", CreateTestLocationResolver());

            Assert.NotEmpty(edges);
            Assert.Contains(edges, e => e.Kind == "Registers" && e.TargetSymbolId.Contains("Service"));
        }

        [Fact]
        public void MediatR_INotificationHandler_EmitsHandlesEdge()
        {
            var source = @"
using MediatR;

public class UserCreatedEvent : INotification { }
public class UserCreatedHandler : INotificationHandler<UserCreatedEvent>
{
    public void Handle(UserCreatedEvent notification) { }
}
";
            var compilation = CreateCompilationWithMediatR(source);
            var adapter = new MediatRAdapter();
            var edges = adapter.Extract(compilation, "snap-b5-mediatr-003", CreateTestLocationResolver());

            Assert.NotEmpty(edges);
            Assert.Contains(edges, e => e.Kind == "Handles" && e.Provenance == "framework_derived");
            Assert.Contains(edges, e => e.TargetSymbolId.Contains("Handle") && e.SourceSymbolId.Contains("UserCreatedEvent"));
        }

        [Fact]
        public void MediatR_RequestHandler_EmitsHandlesEdge()
        {
            var source = @"
using MediatR;

public class GetUserQuery : IRequest<string> { }
public class GetUserHandler : IRequestHandler<GetUserQuery, string>
{
    public string Handle(GetUserQuery request) => ""ok"";
}
";
            var compilation = CreateCompilationWithMediatR(source);
            var adapter = new MediatRAdapter();
            var edges = adapter.Extract(compilation, "snap-b5-mediatr-001", CreateTestLocationResolver());

            Assert.NotEmpty(edges);
            Assert.Contains(edges, e => e.Kind == "Handles" && e.Provenance == "framework_derived");
            Assert.Contains(edges, e => e.TargetSymbolId.Contains("Handle") && e.SourceSymbolId.Contains("GetUserQuery"));
        }

        [Fact]
        public void MediatR_NoReferences_EmitsZeroEdges()
        {
            var source = @"
public class Plain { }
";
            var compilation = CreateCompilation(source);
            var adapter = new MediatRAdapter();
            var edges = adapter.Extract(compilation, "snap-b5-mediatr-002", CreateTestLocationResolver());

            Assert.Empty(edges);
        }

        [Fact]
        public void EfCore_DbSet_EmitsMapsToEdge()
        {
            var source = @"
using Microsoft.EntityFrameworkCore;

public class User { }
public class AppDbContext : DbContext
{
    public DbSet<User> Users { get; set; }
}
";
            var compilation = CreateCompilationWithEfCore(source);
            var adapter = new EfCoreAdapter();
            var edges = adapter.Extract(compilation, "snap-b5-ef-001", CreateTestLocationResolver());

            Assert.NotEmpty(edges);
            Assert.Contains(edges, e => e.Kind == "MapsTo" && e.TargetSymbolId.Contains("User"));

        }

        [Fact]
        public void Serialization_JsonPropertyName_EmitsEdge()
        {
            var source = @"
using System.Text.Json.Serialization;

public class UserProfile
{
    [JsonPropertyName(""email_address"")]
    public string Email { get; set; }
}
";
            var compilation = CreateCompilation(source);
            var adapter = new SerializationAdapter();
            var edges = adapter.Extract(compilation, "snap-b5-serial-001", CreateTestLocationResolver());

            var emailEdges = edges.Where(e =>
                e.Kind == "References" &&
                e.SourceSymbolId.Contains("Email")).ToList();

            Assert.NotEmpty(emailEdges);
        }

        [Fact]
        public void Serialization_NoAttributes_EmitsZeroEdges()
        {
            var source = @"
public class Plain
{
    public string Name { get; set; }
}
";
            var compilation = CreateCompilation(source);
            var adapter = new SerializationAdapter();
            var edges = adapter.Extract(compilation, "snap-b5-serial-002", CreateTestLocationResolver());

            Assert.Empty(edges);
        }

        [Fact]
        public void TestAdapter_FactMethod_EmitsTestedByEdge()
        {
            var source = @"
using Xunit;

public class BarTests
{
    [Fact]
    public void Foo_UsesStringBuilder()
    {
        var x = new System.Text.StringBuilder();
    }
}
";
            var compilation = CreateCompilationWithStubs(source, XunitStubs, "MyProject.Tests");
            var adapter = new TestAdapter();
            var edges = adapter.Extract(compilation, "snap-b5-test-001", CreateTestLocationResolver());

            Assert.NotEmpty(edges);
            Assert.Contains(edges, e => e.Kind == "TestedBy" && e.Provenance == "framework_derived");
            Assert.Contains(edges, e => e.SourceSymbolId.Contains("StringBuilder") && e.TargetSymbolId.Contains("Foo_UsesStringBuilder"));
        }

        [Fact]
        public void TestAdapter_NonTestProject_EmitsZeroEdges()
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(@"
public class Foo
{
    public void Bar() { }
}
", path: "test.cs");

            var compilation = CSharpCompilation.Create(
                "MyProject",
                [syntaxTree],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

            var adapter = new TestAdapter();
            var edges = adapter.Extract(compilation, "snap-b5-test-002", CreateTestLocationResolver());

            Assert.Empty(edges);
        }

        [Fact]
        public void EdgeRecord_FullConstructor_RoundTrips()
        {
            var edge = new EdgeRecord
            {
                SourceSymbolId = "T:Ns.Foo|asm1",
                TargetSymbolId = "T:Ns.Bar|asm1",
                Kind = "RoutesTo",
                Provenance = "framework_derived",
                SnapshotId = "snap-b5-edge-001",
                ExtractorVersion = "aspnetcore-v1",
                SourceDocumentPath = "src/Test.cs",
                SourceStartLine = 10,
                SourceStartColumn = 5,
                SourceEndLine = 10,
                SourceEndColumn = 20,
            };

            Assert.Equal("RoutesTo", edge.Kind);
            Assert.Equal("framework_derived", edge.Provenance);
            Assert.Equal("snap-b5-edge-001", edge.SnapshotId);
            Assert.Equal("aspnetcore-v1", edge.ExtractorVersion);
            Assert.Equal("src/Test.cs", edge.SourceDocumentPath);
            Assert.Equal(10, edge.SourceStartLine);
        }
    }

    public class B7ImpactTraverserTests : IDisposable
    {
        private readonly string _dbPath;
        private SqliteIndexStore? _store;

        public B7ImpactTraverserTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_b7_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            _store?.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private SqliteIndexStore CreateStoreWithEdges(string snapshotId, List<EdgeRecord> edges)
        {
            _store?.Dispose();
            var store = new SqliteIndexStore(_dbPath);
            store.Open(_dbPath);
            store.RunMigrations();
            store.SaveEdges(snapshotId, edges);
            _store = store;
            return store;
        }

        [Fact]
        public void TraceImpact_NonExistentSymbol_ReturnsEmpty()
        {
            var store = CreateStoreWithEdges("snap-b7-001", []);
            var traverser = new ImpactTraverser(store, "snap-b7-001");

            var paths = traverser.TraceImpact("nonexistent", ImpactDirection.Downstream);

            Assert.Empty(paths);
            store.Close();
        }

        [Fact]
        public void TraceImpact_SingleHopDownstream_ReturnsOnePath()
        {
            var snapshotId = "snap-b7-002";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "compiler_proved", SnapshotId = snapshotId, ExtractorVersion = "v1", SourceDocumentPath = "src/A.cs", SourceStartLine = 10 }
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact("M:A|asm1", ImpactDirection.Downstream);

            var path = Assert.Single(paths);
            Assert.False(path.Truncated);
            Assert.Null(path.TruncationReason);
            Assert.Equal(1, path.TotalSteps);
            var hop = Assert.Single(path.Hops);
            Assert.Equal("M:A|asm1", hop.SourceSymbolId);
            Assert.Equal("M:B|asm1", hop.TargetSymbolId);
            Assert.Equal("Calls", hop.EdgeKind);
            Assert.Equal("compiler_proved", hop.Provenance);
            Assert.Equal("src/A.cs", hop.SourceDocument);
            Assert.Equal(10, hop.SourceLine);
            store.Close();
        }

        [Fact]
        public void TraceImpact_MultiHopChain_ReturnsOnePathWithTwoHops()
        {
            var snapshotId = "snap-b7-003";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:B|asm1", TargetSymbolId = "M:C|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact("M:A|asm1", ImpactDirection.Downstream);

            var path = Assert.Single(paths);
            Assert.Equal(2, path.TotalSteps);
            Assert.False(path.Truncated);
            Assert.Equal("M:A|asm1", path.Hops[0].SourceSymbolId);
            Assert.Equal("M:B|asm1", path.Hops[0].TargetSymbolId);
            Assert.Equal("M:B|asm1", path.Hops[1].SourceSymbolId);
            Assert.Equal("M:C|asm1", path.Hops[1].TargetSymbolId);
            store.Close();
        }

        [Fact]
        public void TraceImpact_Branching_ReturnsTwoPaths()
        {
            var snapshotId = "snap-b7-004";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:C|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact("M:A|asm1", ImpactDirection.Downstream);

            Assert.Equal(2, paths.Count);
            Assert.All(paths, p => Assert.Equal(1, p.TotalSteps));
            Assert.Contains(paths, p => p.Hops[0].TargetSymbolId == "M:B|asm1");
            Assert.Contains(paths, p => p.Hops[0].TargetSymbolId == "M:C|asm1");
            store.Close();
        }

        [Fact]
        public void TraceImpact_Upstream_ReturnsTwoPaths()
        {
            var snapshotId = "snap-b7-005";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:C|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact("M:B|asm1", ImpactDirection.Upstream);

            Assert.Equal(2, paths.Count);
            Assert.All(paths, p => Assert.Equal(1, p.TotalSteps));
            Assert.Contains(paths, p => p.Hops[0].SourceSymbolId == "M:C|asm1");
            Assert.Contains(paths, p => p.Hops[0].SourceSymbolId == "M:A|asm1");
            Assert.All(paths, p => Assert.Equal("M:B|asm1", p.Hops[0].TargetSymbolId));
            store.Close();
        }

        [Fact]
        public void TraceImpact_CycleDetection_PreventsInfiniteLoop()
        {
            var snapshotId = "snap-b7-006";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:B|asm1", TargetSymbolId = "M:A|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact("M:A|asm1", ImpactDirection.Downstream, maxDepth: 5);

            var path = Assert.Single(paths);
            Assert.Equal(1, path.TotalSteps);
            Assert.Equal("M:A|asm1", path.Hops[0].SourceSymbolId);
            Assert.Equal("M:B|asm1", path.Hops[0].TargetSymbolId);
            store.Close();
        }

        [Fact]
        public void TraceImpact_MaxDepthTruncation_ReturnsTruncatedPath()
        {
            var snapshotId = "snap-b7-007";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:B|asm1", TargetSymbolId = "M:C|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact("M:A|asm1", ImpactDirection.Downstream, maxDepth: 1);

            var path = Assert.Single(paths);
            Assert.True(path.Truncated);
            Assert.Equal("max depth reached", path.TruncationReason);
            Assert.Equal(1, path.TotalSteps);
            store.Close();
        }

        [Fact]
        public void TraceImpact_EdgeKindFiltering_OnlyReturnsAllowedKinds()
        {
            var snapshotId = "snap-b7-008";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:C|asm1", Kind = "Reads", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact(
                "M:A|asm1", ImpactDirection.Downstream,
                allowedEdgeKinds: ["Calls"]);

            var path = Assert.Single(paths);
            Assert.Equal("M:B|asm1", path.Hops[0].TargetSymbolId);
            Assert.Equal("Calls", path.Hops[0].EdgeKind);
            store.Close();
        }

        [Fact]
        public void TraceImpact_IncludeSourceFalse_SourceFieldsAreNull()
        {
            var snapshotId = "snap-b7-009";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1", SourceDocumentPath = "src/A.cs", SourceStartLine = 10 }
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact("M:A|asm1", ImpactDirection.Downstream, includeSource: false);

            var path = Assert.Single(paths);
            var hop = Assert.Single(path.Hops);
            Assert.Null(hop.SourceDocument);
            Assert.Null(hop.SourceLine);
            Assert.Equal("M:A|asm1", hop.SourceSymbolId);
            Assert.Equal("M:B|asm1", hop.TargetSymbolId);
            store.Close();
        }

        [Fact]
        public void TraceImpact_EmptyEdgeListForExistingSymbol_ReturnsEmpty()
        {
            var snapshotId = "snap-b7-010";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact("M:B|asm1", ImpactDirection.Downstream);

            Assert.Empty(paths);
            store.Close();
        }

        [Fact]
        public void TraceImpact_LeafNodeNoOutgoingEdges_ReturnsEmpty()
        {
            var snapshotId = "snap-b7-011";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:C|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact("M:C|asm1", ImpactDirection.Downstream);

            Assert.Empty(paths);
            store.Close();
        }

        [Fact]
        public void TraceImpact_MultipleEdgeKindsWithFiltering_ReturnsCorrectSubset()
        {
            var snapshotId = "snap-b7-012";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:C|asm1", Kind = "Reads", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:D|asm1", Kind = "Writes", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact(
                "M:A|asm1", ImpactDirection.Downstream,
                allowedEdgeKinds: ["Calls", "Reads"]);

            Assert.Equal(2, paths.Count);
            Assert.Contains(paths, p => p.Hops[0].TargetSymbolId == "M:B|asm1");
            Assert.Contains(paths, p => p.Hops[0].TargetSymbolId == "M:C|asm1");
            Assert.DoesNotContain(paths, p => p.Hops[0].TargetSymbolId == "M:D|asm1");
            store.Close();
        }

        [Fact]
        public void TraceImpact_IncludeSourceDefaultTrue_IncludesSourceFields()
        {
            var snapshotId = "snap-b7-013";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1", SourceDocumentPath = "src/A.cs", SourceStartLine = 42 }
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact("M:A|asm1", ImpactDirection.Downstream);

            var path = Assert.Single(paths);
            var hop = Assert.Single(path.Hops);
            Assert.Equal("src/A.cs", hop.SourceDocument);
            Assert.Equal(42, hop.SourceLine);
            store.Close();
        }
    }

    public class B6ReflectionTests
    {
        private static Compilation CreateCompilation(string source, string path = "test.cs")
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source, path: path);
            return CSharpCompilation.Create(
                "TestAssembly",
                [syntaxTree],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        }

        [Fact]
        public void TypeOf_EmitsReflectionTypeRefEdge()
        {
            var source = @"
class Foo { }
class Bar {
    void M() { var t = typeof(Foo); }
}";
            var compilation = CreateCompilation(source);
            var extractor = new ReflectionExtractor(compilation, "snap-b6-typeof", "/");
            var edges = extractor.Extract();

            var reflectionEdges = edges.Where(e => e.Kind == EdgeKind.ReflectionTypeRef.ToString()).ToList();
            var edge = Assert.Single(reflectionEdges);
            Assert.Equal("compiler_proved", edge.Provenance);
            Assert.Contains("Foo", edge.TargetSymbolId);
            Assert.Contains("M", edge.SourceSymbolId);
        }

        [Fact]
        public void NameOf_EmitsReflectionMemberRefEdge()
        {
            var source = @"
class Foo {
    public void Bar() { }
}
class Baz {
    void M() { _ = nameof(Foo.Bar); }
}";
            var compilation = CreateCompilation(source);
            var extractor = new ReflectionExtractor(compilation, "snap-b6-nameof", "/");
            var edges = extractor.Extract();

            var reflectionEdges = edges.Where(e => e.Kind == EdgeKind.ReflectionMemberRef.ToString()).ToList();
            var edge = Assert.Single(reflectionEdges);
            Assert.Contains("M", edge.SourceSymbolId);
        }

        [Fact]
        public void StringLiteral_MatchingTypeName_EmitsNameCandidateEdge()
        {
            var source = @"
class SomeKnownType { }
class Bar {
    void M() { var s = ""SomeKnownType""; }
}";
            var compilation = CreateCompilation(source);
            var extractor = new ReflectionExtractor(compilation, "snap-b6-stringlit", "/");
            var edges = extractor.Extract();

            var nameEdges = edges.Where(e => e.Kind == EdgeKind.ReflectionNameCandidate.ToString()).ToList();
            var edge = Assert.Single(nameEdges);
            Assert.Equal("name_candidate", edge.Provenance);
            Assert.Contains("SomeKnownType", edge.TargetSymbolId);
            Assert.Contains("M", edge.SourceSymbolId);
        }

        [Fact]
        public void TypeGetType_EmitsUnknownEdge()
        {
            var source = @"
class Bar {
    void M() { var t = System.Type.GetType(""Something""); }
}";
            var compilation = CreateCompilation(source);
            var extractor = new ReflectionExtractor(compilation, "snap-b6-unknown", "/");
            var edges = extractor.Extract();

            var unknownEdges = edges.Where(e => e.Kind == EdgeKind.ReflectionTargetUnknown.ToString()).ToList();
            var edge = Assert.Single(unknownEdges);
            Assert.Equal("runtime_unknown", edge.Provenance);
            Assert.Contains("M", edge.SourceSymbolId);
        }

        [Fact]
        public void NoReflection_EmitsZeroEdges()
        {
            var source = @"
class Foo {
    void M() { int x = 42; }
}";
            var compilation = CreateCompilation(source);
            var extractor = new ReflectionExtractor(compilation, "snap-b6-none", "/");
            var edges = extractor.Extract();

            Assert.Empty(edges);
        }

        [Fact]
        public void NameOf_UnresolvableExpression_EmitsNoEdges()
        {
            var source = @"
class Bar {
    void M() { _ = nameof(UnknownType.UnknownMember); }
}";
            var compilation = CreateCompilation(source);
            var extractor = new ReflectionExtractor(compilation, "snap-b6-nameof-unresolved", "/");
            var edges = extractor.Extract();

            Assert.Empty(edges);
        }

        [Fact]
        public void StringLiteral_MatchingMemberName_EmitsNameCandidateEdge()
        {
            var source = @"
class Foo {
    public void Bar() { }
}
class Baz {
    void M() { var s = ""Bar""; }
}";
            var compilation = CreateCompilation(source);
            var extractor = new ReflectionExtractor(compilation, "snap-b6-stringlit-member", "/");
            var edges = extractor.Extract();

            var nameEdges = edges.Where(e => e.Kind == EdgeKind.ReflectionNameCandidate.ToString()).ToList();
            var memberEdge = nameEdges.FirstOrDefault(e => e.TargetSymbolId.Contains("Bar"));
            Assert.NotNull(memberEdge);
            Assert.Equal("name_candidate", memberEdge.Provenance);
        }

        [Fact]
        public void MultipleReflectionPatterns_EmitsMultipleEdges()
        {
            var source = @"
class TargetType { }
class Source {
    void M() {
        var t = typeof(TargetType);
        _ = nameof(TargetType);
    }
}";
            var compilation = CreateCompilation(source);
            var extractor = new ReflectionExtractor(compilation, "snap-b6-multi", "/");
            var edges = extractor.Extract();

            var typeRefEdges = edges.Where(e => e.Kind == EdgeKind.ReflectionTypeRef.ToString()).ToList();
            var memberRefEdges = edges.Where(e => e.Kind == EdgeKind.ReflectionMemberRef.ToString()).ToList();

            Assert.NotEmpty(typeRefEdges);
            Assert.NotEmpty(memberRefEdges);
            Assert.True(edges.Count >= 2);
        }

        [Fact]
        public void ActivatorCreateInstance_EmitsReflectionTargetUnknownEdge()
        {
            var source = @"
class Target { }
class Source {
    void M() { var x = System.Activator.CreateInstance<Target>(); }
}";

            var systemRuntimePath = typeof(System.Activator).Assembly.Location;
            var syntaxTree = CSharpSyntaxTree.ParseText(source, path: "test.cs");
            var compilation = CSharpCompilation.Create(
                "TestAssembly",
                [syntaxTree],
                [
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(systemRuntimePath)
                ]);
            var extractor = new ReflectionExtractor(compilation, "snap-b6-activator", "/");
            var edges = extractor.Extract();

            var unknownEdges = edges.Where(e => e.Kind == EdgeKind.ReflectionTargetUnknown.ToString()).ToList();
            Assert.NotEmpty(unknownEdges);
            Assert.Contains(unknownEdges, e => e.Provenance == "runtime_unknown");
        }
    }

    public class C16SimulationTests : IDisposable
    {
        private readonly string _dbPath;
        private SqliteIndexStore? _store;

        public C16SimulationTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_c16_sim_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            _store?.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private SqliteIndexStore CreateStoreWithEdges(string snapshotId, List<EdgeRecord> edges)
        {
            _store?.Dispose();
            var store = new SqliteIndexStore(_dbPath);
            store.Open(_dbPath);
            store.RunMigrations();
            store.SaveEdges(snapshotId, edges);
            _store = store;
            return store;
        }

        [Fact]
        public void SimulateRename_CallerEdge_ReportsCallerSymbol()
        {
            const string snapId = "snap-c16-sim-001";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:A|asm",
                    TargetSymbolId = "M:B|asm",
                    Kind = "Calls",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            var engine = new SimulationEngine(store, store, snapId);

            var report = engine.SimulateRename("M:B|asm", "BRenamed");

            Assert.Equal("rename", report.SimulationType);
            Assert.Contains(report.Items, i => i.SymbolId == "M:A|asm");
            store.Close();
        }

        [Fact]
        public void SimulateRename_OverrideEdge_ReportsOverrideDeclaration()
        {
            const string snapId = "snap-c16-sim-002";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:C|asm",
                    TargetSymbolId = "M:B|asm",
                    Kind = "Overrides",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            var engine = new SimulationEngine(store, store, snapId);

            var report = engine.SimulateRename("M:B|asm", "BRenamed");

            Assert.Contains(report.Items, i => i.SymbolId == "M:C|asm" && i.EdgeKind == "Overrides");
            store.Close();
        }

        [Fact]
        public void SimulateRename_NoCallers_ReturnsEmptyItems()
        {
            const string snapId = "snap-c16-sim-003";
            var store = CreateStoreWithEdges(snapId, []);
            var engine = new SimulationEngine(store, store, snapId);

            var report = engine.SimulateRename("M:B|asm", "BRenamed");

            Assert.Empty(report.Items);
            Assert.Equal(0, report.AffectedCount);
            store.Close();
        }

        [Fact]
        public void SimulateMove_CallerWithDocumentPath_ReportsDocumentPath()
        {
            const string snapId = "snap-c16-sim-004";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:A|asm",
                    TargetSymbolId = "M:B|asm",
                    Kind = "Calls",
                    Provenance = "compiler_proved",
                    SnapshotId = snapId,
                    ExtractorVersion = "v1",
                    SourceDocumentPath = "src/A.cs",
                    SourceStartLine = 10,
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            var engine = new SimulationEngine(store, store, snapId);

            var report = engine.SimulateMove("M:B|asm", "NewNs");

            var item = Assert.Single(report.Items);
            Assert.Equal("src/A.cs", item.DocumentPath);
            Assert.Equal(10, item.Line);
            store.Close();
        }

        [Fact]
        public void SimulateRemove_DependentWithRegistration_ReportsOrphanedRegistration()
        {
            const string snapId = "snap-c16-sim-005";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "T:Startup|asm",
                    TargetSymbolId = "M:B|asm",
                    Kind = "Registers",
                },
                new() {
                    SourceSymbolId = "M:A|asm",
                    TargetSymbolId = "M:B|asm",
                    Kind = "Calls",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            var engine = new SimulationEngine(store, store, snapId);

            var report = engine.SimulateRemove("M:B|asm");

            Assert.Contains(report.Items, i => i.EdgeKind == "Registers");
            store.Close();
        }

        [Fact]
        public void SimulateRemove_SymbolWithTest_ReportsOrphanedTest()
        {
            const string snapId = "snap-c16-sim-006";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:FooTest|asm",
                    TargetSymbolId = "M:B|asm",
                    Kind = "TestedBy",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            var engine = new SimulationEngine(store, store, snapId);

            var report = engine.SimulateRemove("M:B|asm");

            Assert.Contains(report.Items, i => i.EdgeKind == "TestedBy");
            store.Close();
        }
    }

    public class C16AuditTests : IDisposable
    {
        private readonly string _dbPath;
        private SqliteIndexStore? _store;

        public C16AuditTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_c16_aud_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            _store?.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private SqliteIndexStore CreateStoreWithEdges(string snapshotId, List<EdgeRecord> edges)
        {
            _store?.Dispose();
            var store = new SqliteIndexStore(_dbPath);
            store.Open(_dbPath);
            store.RunMigrations();
            store.SaveEdges(snapshotId, edges);
            _store = store;
            return store;
        }

        private void CreateStoreWithSymbols(SqliteIndexStore store, string snapshotId, List<string> symbolIds, string? metadataJson = null)
        {
            // Seed FK references needed by SaveDeclarations
            using (var fkConn = new SqliteConnection($"Data Source={_dbPath}"))
            {
                fkConn.Open();
                using var fkCmd = fkConn.CreateCommand();
                fkCmd.CommandText = @"
                    INSERT OR IGNORE INTO workspaces (workspace_id, git_root, solution_path)
                    VALUES ('test-ws', '/fake/root', 'test.sln');
                    INSERT OR IGNORE INTO snapshots (snapshot_id, workspace_id, built_at_utc)
                    VALUES (@sid, 'test-ws', '2026-01-01T00:00:00Z');
                    INSERT OR IGNORE INTO documents (document_id, relative_path)
                    VALUES ('doc-1', 'test.cs');
                    INSERT OR IGNORE INTO document_versions (document_version_id, document_id, content_hash)
                    VALUES ('doc-v1', 'doc-1', 'hash');
                ";
                fkCmd.Parameters.AddWithValue("@sid", snapshotId);
                fkCmd.ExecuteNonQuery();
            }

            var declarations = symbolIds.Select(id =>
            {
                var sid = SymbolId.Parse(id);
                return new SymbolDeclaration
                {
                    SymbolId = sid,
                    Kind = IndexedSymbolKind.Method,
                    DocumentVersionId = "doc-v1",
                    FullSpan = new DeclarationSpan(null, null),
                    SignatureSpan = new DeclarationSpan(null, null),
                    BodySpan = new DeclarationSpan(null, null),
                    NameSpan = new DeclarationSpan(null, null),
                    MetadataJson = metadataJson ?? "{\"accessibility\":\"public\"}"
                };
            }).ToList();
            store.SaveDeclarations(snapshotId, declarations);
        }

        [Fact]
        public void DeadSymbol_NoIncomingEdges_Flagged()
        {
            const string snapId = "snap-c16-aud-001";
            var store = CreateStoreWithEdges(snapId, []);
            CreateStoreWithSymbols(store, snapId, ["M:A|asm"]);
            var engine = new AuditEngine(store, snapId);

            var report = engine.RunAudit(new AuditOptions(["dead-symbol"]));

            Assert.Contains(report.Findings, f => f.Check == "dead-symbol" && f.SymbolId == "M:A|asm");
            store.Close();
        }

        [Fact]
        public void DeadSymbol_OnlyTestedByIncoming_StillFlagged()
        {
            const string snapId = "snap-c16-aud-002";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:Test|asm",
                    TargetSymbolId = "M:A|asm",
                    Kind = "TestedBy",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            CreateStoreWithSymbols(store, snapId, ["M:A|asm", "M:Test|asm"]);
            var engine = new AuditEngine(store, snapId);

            var report = engine.RunAudit(new AuditOptions(["dead-symbol"]));

            Assert.Contains(report.Findings, f => f.Check == "dead-symbol" && f.SymbolId == "M:A|asm");
            store.Close();
        }

        [Fact]
        public void DeadSymbol_HasCallsIncoming_NotFlagged()
        {
            const string snapId = "snap-c16-aud-003";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:Caller|asm",
                    TargetSymbolId = "M:A|asm",
                    Kind = "Calls",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            CreateStoreWithSymbols(store, snapId, ["M:A|asm", "M:Caller|asm"]);
            var engine = new AuditEngine(store, snapId);

            var report = engine.RunAudit(new AuditOptions(["dead-symbol"]));

            Assert.DoesNotContain(report.Findings, f => f.Check == "dead-symbol" && f.SymbolId == "M:A|asm");
            store.Close();
        }

        [Fact]
        public void UntestedSurface_SymbolWithNoTestedBy_Flagged()
        {
            const string snapId = "snap-c16-aud-004";
            var store = CreateStoreWithEdges(snapId, []);
            CreateStoreWithSymbols(store, snapId, ["M:A|asm"],
                metadataJson: "{\"accessibility\":\"public\"}");
            var engine = new AuditEngine(store, snapId);

            var report = engine.RunAudit(new AuditOptions(["untested-surface"]));

            Assert.Contains(report.Findings, f => f.Check == "untested-surface" && f.SymbolId == "M:A|asm");
            store.Close();
        }

        [Fact]
        public void UntestedSurface_SymbolWithTestedByEdge_NotFlagged()
        {
            const string snapId = "snap-c16-aud-005";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:Test|asm",
                    TargetSymbolId = "M:A|asm",
                    Kind = "TestedBy",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            CreateStoreWithSymbols(store, snapId, ["M:A|asm", "M:Test|asm"],
                metadataJson: "{\"accessibility\":\"public\"}");
            var engine = new AuditEngine(store, snapId);

            var report = engine.RunAudit(new AuditOptions(["untested-surface"]));

            Assert.DoesNotContain(report.Findings, f => f.Check == "untested-surface" && f.SymbolId == "M:A|asm");
            store.Close();
        }

        [Fact]
        public void UnregisteredImpl_ImplementsWithoutRegisters_Flagged()
        {
            const string snapId = "snap-c16-aud-006";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "T:Impl|asm",
                    TargetSymbolId = "T:IFoo|asm",
                    Kind = "Implements",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            CreateStoreWithSymbols(store, snapId, ["T:Impl|asm", "T:IFoo|asm"]);
            var engine = new AuditEngine(store, snapId);

            var report = engine.RunAudit(new AuditOptions(["unregistered-impl"]));

            Assert.Contains(report.Findings, f => f.Check == "unregistered-impl" && f.SymbolId == "T:Impl|asm");
            store.Close();
        }

        [Fact]
        public void UnregisteredImpl_ImplementsWithRegisters_NotFlagged()
        {
            const string snapId = "snap-c16-aud-007";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "T:Impl|asm",
                    TargetSymbolId = "T:IFoo|asm",
                    Kind = "Implements",
                },
                new() {
                    SourceSymbolId = "T:Startup|asm",
                    TargetSymbolId = "T:Impl|asm",
                    Kind = "Registers",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            CreateStoreWithSymbols(store, snapId, ["T:Impl|asm", "T:IFoo|asm", "T:Startup|asm"]);
            var engine = new AuditEngine(store, snapId);

            var report = engine.RunAudit(new AuditOptions(["unregistered-impl"]));

            Assert.DoesNotContain(report.Findings, f => f.Check == "unregistered-impl" && f.SymbolId == "T:Impl|asm");
            store.Close();
        }

        [Fact]
        public void HighFanOut_ExceedsThreshold_Flagged()
        {
            const string snapId = "snap-c16-aud-008";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:God|asm",
                    TargetSymbolId = "M:T1|asm",
                    Kind = "Calls",
                },
                new() {
                    SourceSymbolId = "M:God|asm",
                    TargetSymbolId = "M:T2|asm",
                    Kind = "Calls",
                },
                new() {
                    SourceSymbolId = "M:God|asm",
                    TargetSymbolId = "M:T3|asm",
                    Kind = "Calls",
                },
                new() {
                    SourceSymbolId = "M:God|asm",
                    TargetSymbolId = "M:T4|asm",
                    Kind = "Calls",
                },
                new() {
                    SourceSymbolId = "M:God|asm",
                    TargetSymbolId = "M:T5|asm",
                    Kind = "Calls",
                },
            };
            var store = CreateStoreWithEdges(snapId, edges);
            CreateStoreWithSymbols(store, snapId, ["M:God|asm"]);
            var engine = new AuditEngine(store, snapId);

            var report = engine.RunAudit(new AuditOptions(["high-fan-out"], fanOutThreshold: 3));

            Assert.Contains(report.Findings, f => f.Check == "high-fan-out" && f.SymbolId == "M:God|asm");
            store.Close();
        }

        [Fact]
        public void HighFanOut_BelowThreshold_NotFlagged()
        {
            const string snapId = "snap-c16-aud-009";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:Lean|asm",
                    TargetSymbolId = "M:T1|asm",
                    Kind = "Calls",
                },
                new() {
                    SourceSymbolId = "M:Lean|asm",
                    TargetSymbolId = "M:T2|asm",
                    Kind = "Calls",
                },
            };
            var store = CreateStoreWithEdges(snapId, edges);
            CreateStoreWithSymbols(store, snapId, ["M:Lean|asm"]);
            var engine = new AuditEngine(store, snapId);

            var report = engine.RunAudit(new AuditOptions(["high-fan-out"], fanOutThreshold: 3));

            Assert.DoesNotContain(report.Findings, f => f.Check == "high-fan-out" && f.SymbolId == "M:Lean|asm");
            store.Close();
        }
    }
}

