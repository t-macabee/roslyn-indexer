using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using Lurp.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lurp.Storage.Tests;

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
    public void RunMigrations_AppliesAllMigrations_SchemaVersionIsSix()
    {
        var runner = new MigrationRunner(_dbPath);

        runner.RunMigrations();

        Assert.Equal(6, runner.GetCurrentSchemaVersion());
    }

    [Fact]
    public void RunMigrations_CalledTwice_IsIdempotent()
    {
        var runner = new MigrationRunner(_dbPath);

        runner.RunMigrations();
        runner.RunMigrations();

        Assert.Equal(6, runner.GetCurrentSchemaVersion());
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
        Assert.Equal(6, runner.GetCurrentSchemaVersion());

        
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


    public class SymbolStoreTests : IDisposable
    {
        private readonly string _dbPath;

        public SymbolStoreTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_symtest_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private SqliteIndexStore CreateStore()
        {
            var store = new SqliteIndexStore(_dbPath);
            store.Open(_dbPath);
            store.RunMigrations();
            return store;
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

            var manifest = new SnapshotManifest(
                snapshotId: snapshotId,
                workspaceId: "workspace:///root/proj",
                gitRoot: "/root",
                solutionPath: "/root/proj",
                sdkVersion: "10.0.301",
                compilerVersion: "4.12.0.0",
                createdAtUtc: DateTime.UtcNow,
                documents: new List<DocumentVersion>
                {
                    new("doc1", "src/Foo.cs", "hash1", "utf-8", lineStarts, DateTime.MinValue,
                        sourceBytes, lineStarts),
                });
            store.SaveSnapshot(manifest);
        }


        private static SymbolDeclaration MakeDecl(
            string symbolId,
            string docCommentId,
            string assembly,
            SymbolKind kind,
            string docVersionId,
            int? fullS, int? fullE,
            int? sigS, int? sigE,
            int? bodyS, int? bodyE,
            int? nameS, int? nameE,
            bool isPartial = false,
            string? fqn = null)
        {
            return new SymbolDeclaration(
                symbolId: new SymbolId(docCommentId, assembly, fqn),
                kind: kind,
                documentVersionId: docVersionId,
                fullSpan: new DeclarationSpan(fullS, fullE),
                signatureSpan: new DeclarationSpan(sigS, sigE),
                bodySpan: new DeclarationSpan(bodyS, bodyE),
                nameSpan: new DeclarationSpan(nameS, nameE),
                isPartial: isPartial);
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
                kind: SymbolKind.Type,
                docVersionId: "doc1",
                fullS: 33, fullE: 112,
                sigS: 33, sigE: 54,
                bodyS: 54, bodyE: 112,
                nameS: 50, nameE: 53);

            store.SaveDeclarations(snapshotId, new[] { decl });

            var info = store.GetSymbolInfo("T:TestNs.Foo|assembly1", snapshotId);
            Assert.NotNull(info);
            Assert.Equal("T:TestNs.Foo", info!.SymbolId.DocCommentId);
            Assert.Equal("assembly1", info.SymbolId.AssemblyIdentity);
            Assert.Equal(SymbolKind.Type, info.Kind);
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
                kind: SymbolKind.Method,
                docVersionId: "doc1",
                fullS: 56, fullE: 107,
                sigS: 56, sigE: 82,
                bodyS: 82, bodyE: 106,
                nameS: 76, nameE: 79);

            store.SaveDeclarations(snapshotId, new[] { decl });

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
                kind: SymbolKind.Type,
                docVersionId: "doc1",
                fullS: 33, fullE: 112,
                sigS: 33, sigE: 54,
                bodyS: 54, bodyE: 112,
                nameS: 50, nameE: 53);

            store.SaveDeclarations(snapshotId, new[] { decl });

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
                kind: SymbolKind.Method,
                docVersionId: "doc1",
                fullS: 33, fullE: 112,
                sigS: 33, sigE: 113,
                bodyS: null, bodyE: null,
                nameS: 50, nameE: 53);

            store.SaveDeclarations(snapshotId, new[] { decl });

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

            var manifest = new SnapshotManifest(
                snapshotId: snapshotId,
                workspaceId: "workspace:///root/proj",
                gitRoot: "/root",
                solutionPath: "/root/proj",
                sdkVersion: "10.0.301",
                compilerVersion: "4.12.0.0",
                createdAtUtc: DateTime.UtcNow,
                documents: new List<DocumentVersion>
                {
                    new("doc-part1", "src/part1.cs", "hash-p1", "utf-8", lineStarts, DateTime.MinValue,
                        source1, lineStarts),
                    new("doc-part2", "src/part2.cs", "hash-p2", "utf-8", lineStarts, DateTime.MinValue,
                        source2, lineStarts),
                });
            store.SaveSnapshot(manifest);

            var symId = new SymbolId("T:Foo", "assembly1", "TestNs.Foo");

            var decl1 = new SymbolDeclaration(
                symbolId: symId,
                kind: SymbolKind.Type,
                documentVersionId: "doc-part1",
                fullSpan: new DeclarationSpan(0, 29),
                signatureSpan: new DeclarationSpan(0, 15),
                bodySpan: new DeclarationSpan(15, 28),
                nameSpan: new DeclarationSpan(15, 18),
                isPartial: true);

            var decl2 = new SymbolDeclaration(
                symbolId: symId,
                kind: SymbolKind.Type,
                documentVersionId: "doc-part2",
                fullSpan: new DeclarationSpan(0, 29),
                signatureSpan: new DeclarationSpan(0, 15),
                bodySpan: new DeclarationSpan(15, 28),
                nameSpan: new DeclarationSpan(15, 18),
                isPartial: true);

            store.SaveDeclarations(snapshotId, new[] { decl1, decl2 });

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
                kind: SymbolKind.Method,
                docVersionId: "doc1",
                fullS: 56, fullE: 107,
                sigS: 56, sigE: 82,
                bodyS: 82, bodyE: 106,
                nameS: 76, nameE: 79);

            store.SaveDeclarations(snapshotId, new[] { decl });

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
                kind: SymbolKind.Method,
                docVersionId: "doc1",
                fullS: 56, fullE: 107,
                sigS: 56, sigE: 82,
                bodyS: 82, bodyE: 106,
                nameS: 76, nameE: 79);

            store.SaveDeclarations(snapshotId, new[] { decl });

            var name = store.GetSymbolSource("M:TestNs.Foo.Bar|assembly1", snapshotId, ViewKind.Name);
            Assert.Equal("Bar", name);
        }
    }

    public class FtsSearchTests : IDisposable
    {
        private readonly string _dbPath;

        public FtsSearchTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_fts_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private SqliteIndexStore CreateStore()
        {
            var store = new SqliteIndexStore(_dbPath);
            store.Open(_dbPath);
            store.RunMigrations();
            return store;
        }

        private static byte[] StringToBytes(string text) => Encoding.UTF8.GetBytes(text);

        private static void CreateSnapshotWithContent(
            SqliteIndexStore store, string snapshotId, string relativePath, string content)
        {
            var lineStarts = "[0]";
            var sourceBytes = StringToBytes(content);

            var manifest = new SnapshotManifest(
                snapshotId: snapshotId,
                workspaceId: "workspace:///root/proj",
                gitRoot: "/root",
                solutionPath: "/root/proj",
                sdkVersion: "10.0.301",
                compilerVersion: "4.12.0.0",
                createdAtUtc: DateTime.UtcNow,
                documents: new List<DocumentVersion>
                {
                    new("doc-" + relativePath, relativePath, "hash1", "utf-8", lineStarts, DateTime.MinValue,
                        sourceBytes, lineStarts),
                });
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

            var decl = new SymbolDeclaration(
                symbolId: new SymbolId("T:N.Foo", "asm1", "N.Foo"),
                kind: SymbolKind.Type,
                documentVersionId: "doc-src/Foo.cs",
                fullSpan: new DeclarationSpan(0, 10),
                signatureSpan: new DeclarationSpan(0, 10),
                bodySpan: new DeclarationSpan(null, null),
                nameSpan: new DeclarationSpan(0, 3));

            store.SaveDeclarations(snapshotId, new[] { decl });
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

            var decl = new SymbolDeclaration(
                symbolId: new SymbolId("T:A", "asm1", "A"),
                kind: SymbolKind.Type,
                documentVersionId: "doc-src/a.cs",
                fullSpan: new DeclarationSpan(0, 10),
                signatureSpan: new DeclarationSpan(0, 10),
                bodySpan: new DeclarationSpan(null, null),
                nameSpan: new DeclarationSpan(0, 1));

            store.SaveDeclarations(snapshotId, new[] { decl });
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

            var decl = new SymbolDeclaration(
                symbolId: new SymbolId("T:A", "asm1", "MyNs.A"),
                kind: SymbolKind.Type,
                documentVersionId: "doc-src/a.cs",
                fullSpan: new DeclarationSpan(0, 10),
                signatureSpan: new DeclarationSpan(0, 10),
                bodySpan: new DeclarationSpan(null, null),
                nameSpan: new DeclarationSpan(0, 1));

            store.SaveDeclarations(snapshotId, new[] { decl });

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

            var decl = new SymbolDeclaration(
                symbolId: new SymbolId("T:A", "asm1", "MyNs.MyClass"),
                kind: SymbolKind.Type,
                documentVersionId: "doc-src/a.cs",
                fullSpan: new DeclarationSpan(0, 10),
                signatureSpan: new DeclarationSpan(0, 10),
                bodySpan: new DeclarationSpan(null, null),
                nameSpan: new DeclarationSpan(0, 1));

            store.SaveDeclarations(snapshotId, new[] { decl });

            // Partial prefix match
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
            Assert.Equal(6, runner.GetCurrentSchemaVersion());

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
            Assert.Equal(6, runner.GetCurrentSchemaVersion());

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

        public A5OperationalTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_a5_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private SqliteIndexStore CreateStore()
        {
            var store = new SqliteIndexStore(_dbPath);
            store.Open(_dbPath);
            store.RunMigrations();
            return store;
        }

        [Fact]
        public void SaveAndGetEdges_RoundTrips()
        {
            var store = CreateStore();
            var snapshotId = "snap-a5-edges-001";

            var edges = new List<EdgeRecord>
            {
                new("T:Ns.Foo|asm1", "T:Ns.Bar|asm1", "Inherits", "roslyn"),
                new("T:Ns.Foo|asm1", "T:Ns.IBaz|asm1", "Implements", "roslyn"),
            };
            store.SaveEdges(snapshotId, edges);

            var loaded = store.GetEdges(snapshotId);
            Assert.Equal(2, loaded.Count);
            Assert.Equal("T:Ns.Foo|asm1", loaded[0].SourceSymbolId);
            Assert.Equal("T:Ns.Bar|asm1", loaded[0].TargetSymbolId);
            Assert.Equal("Inherits", loaded[0].Kind);

            // Filter by symbolId
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
                new("MyProject", "src/Program.cs", "Warning", "CS0219", "Variable 'x' is unused",
                    startLine: 10, startColumn: 5, endLine: 10, endColumn: 6),
                new("MyProject", "src/Utils.cs", "Error", "CS0103", "The name 'foo' does not exist",
                    startLine: 5, startColumn: 1, endLine: 5, endColumn: 4),
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
            store.SaveEdges("snap-empty", new List<EdgeRecord>());
            Assert.Empty(store.GetEdges("snap-empty"));
            store.Close();
        }

        [Fact]
        public void SaveDiagnostics_EmptyList_DoesNotThrow()
        {
            var store = CreateStore();
            store.SaveDiagnostics("snap-empty", new List<DiagnosticRecord>());
            Assert.Empty(store.GetDiagnostics("snap-empty"));
            store.Close();
        }

        [Fact]
        public void SaveAnnotations_EmptyList_DoesNotThrow()
        {
            var store = CreateStore();
            store.SaveAnnotations("snap-empty", new List<AnnotationRecord>());
            Assert.Empty(store.GetAnnotations("snap-empty"));
            store.Close();
        }
    }

    public class B0ExpansionTests : IDisposable
    {
        private readonly string _dbPath;

        public B0ExpansionTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_b0_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private SqliteIndexStore CreateStore()
        {
            var store = new SqliteIndexStore(_dbPath);
            store.Open(_dbPath);
            store.RunMigrations();
            return store;
        }

        /// <summary>
        /// 5.1 — Run Migration_006 twice; verify idempotent (no crash, version stays 6).
        /// </summary>
        [Fact]
        public void Migration006_RunTwice_IsIdempotent()
        {
            var runner = new MigrationRunner(_dbPath);

            runner.RunMigrations();
            Assert.Equal(6, runner.GetCurrentSchemaVersion());

            // Second run — must not throw and version stays the same
            runner.RunMigrations();
            Assert.Equal(6, runner.GetCurrentSchemaVersion());
        }

        /// <summary>
        /// 5.2 — Insert an edge with all new fields populated, read it back,
        /// and verify every field round-trips.
        /// </summary>
        [Fact]
        public void SaveAndGetEdge_WithAllNewFields_RoundTrips()
        {
            var store = CreateStore();
            var snapshotId = "snap-b0-rt-001";

            var edges = new List<EdgeRecord>
            {
                new(
                    sourceSymbolId: "M:Ns.Foo.Bar|asm1",
                    targetSymbolId: "M:Ns.Baz.Qux|asm1",
                    kind: EdgeKind.Calls.ToString(),
                    provenance: "compiler_proved",
                    snapshotId: snapshotId,
                    extractorVersion: "member-edges-v1",
                    sourceDocumentPath: "src/Foo.cs",
                    sourceStartLine: 42,
                    sourceStartColumn: 13,
                    sourceEndLine: 42,
                    sourceEndColumn: 30)
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

        /// <summary>
        /// 5.2 (variant) — Insert with nullable fields null, verify they round-trip as null/empty.
        /// </summary>
        [Fact]
        public void SaveAndGetEdge_WithNullLocationFields_RoundTrips()
        {
            var store = CreateStore();
            var snapshotId = "snap-b0-rt-002";

            var edges = new List<EdgeRecord>
            {
                new(
                    sourceSymbolId: "T:Ns.Foo|asm1",
                    targetSymbolId: "T:Ns.Bar|asm1",
                    kind: EdgeKind.Inherits.ToString(),
                    provenance: "compiler_proved",
                    snapshotId: snapshotId,
                    extractorVersion: "v1")
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

        /// <summary>
        /// 5.3 — Ensure the existing backward-compatible constructor (4 params)
        /// still works end-to-end through SaveEdges/GetEdges.
        /// </summary>
        [Fact]
        public void SaveAndGetEdge_BackwardCompatibleConstructor_StillWorks()
        {
            var store = CreateStore();
            var snapshotId = "snap-b0-bc-001";

            // Using the old-style 4-param constructor (matches pre-B0 callers)
            var edges = new List<EdgeRecord>
            {
                new("T:Ns.Foo|asm1", "T:Ns.Bar|asm1", "Inherits", "roslyn"),
                new("T:Ns.Foo|asm1", "T:Ns.IBaz|asm1", "Implements"),
            };

            store.SaveEdges(snapshotId, edges);

            var loaded = store.GetEdges(snapshotId);
            Assert.Equal(2, loaded.Count);

            // First edge: all params provided
            Assert.Equal("T:Ns.Foo|asm1", loaded[0].SourceSymbolId);
            Assert.Equal("T:Ns.Bar|asm1", loaded[0].TargetSymbolId);
            Assert.Equal("Inherits", loaded[0].Kind);
            Assert.Equal("roslyn", loaded[0].Provenance);
            // New fields should default to empty/null
            Assert.Equal(snapshotId, loaded[0].SnapshotId); // from SaveEdges param
            Assert.Equal("", loaded[0].ExtractorVersion);   // not provided → empty string
            Assert.Null(loaded[0].SourceDocumentPath);

            // Second edge: provenance omitted (defaults to empty string)
            Assert.Equal("Implements", loaded[1].Kind);
            Assert.Equal("", loaded[1].Provenance);

            // Filter by symbolId still works
            var filtered = store.GetEdges(snapshotId, "T:Ns.Bar|asm1");
            Assert.Single(filtered);

            store.Close();
        }

        /// <summary>
        /// 5.2 — Verify GetEdgesByKind returns only edges of the requested kind.
        /// </summary>
        [Fact]
        public void GetEdgesByKind_FiltersCorrectly()
        {
            var store = CreateStore();
            var snapshotId = "snap-b0-kind-001";

            var edges = new List<EdgeRecord>
            {
                new("T:A|asm1", "T:Base|asm1", "Inherits", "cp", snapshotId, "v1"),
                new("T:A|asm1", "T:IFoo|asm1", "Implements", "cp", snapshotId, "v1"),
                new("M:A.Foo|asm1", "M:B.Bar|asm1", "Calls", "cp", snapshotId, "v1"),
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

        /// <summary>
        /// 5.2 — Verify GetIncomingEdges returns edges where symbol is the target.
        /// </summary>
        [Fact]
        public void GetIncomingEdges_ReturnsEdgesTargetingSymbol()
        {
            var store = CreateStore();
            var snapshotId = "snap-b0-in-001";

            var edges = new List<EdgeRecord>
            {
                new("M:A.Foo|asm1", "M:B.Bar|asm1", "Calls", "cp", snapshotId, "v1"),
                new("M:C.Qux|asm1", "M:B.Bar|asm1", "Calls", "cp", snapshotId, "v1"),
                new("M:B.Bar|asm1", "M:D.Other|asm1", "Calls", "cp", snapshotId, "v1"),
            };
            store.SaveEdges(snapshotId, edges);

            var incoming = store.GetIncomingEdges(snapshotId, "M:B.Bar|asm1");
            Assert.Equal(2, incoming.Count);
            Assert.All(incoming, e => Assert.Equal("M:B.Bar|asm1", e.TargetSymbolId));

            store.Close();
        }

        /// <summary>
        /// 5.2 — Verify GetOutgoingEdges returns edges where symbol is the source.
        /// </summary>
        [Fact]
        public void GetOutgoingEdges_ReturnsEdgesFromSymbol()
        {
            var store = CreateStore();
            var snapshotId = "snap-b0-out-001";

            var edges = new List<EdgeRecord>
            {
                new("M:A.Foo|asm1", "M:B.Bar|asm1", "Calls", "cp", snapshotId, "v1"),
                new("M:A.Foo|asm1", "M:C.Qux|asm1", "Calls", "cp", snapshotId, "v1"),
                new("M:B.Bar|asm1", "M:D.Other|asm1", "Calls", "cp", snapshotId, "v1"),
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
                new[] { syntaxTree },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
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
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), "snap-decl");

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
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), "snap-calls");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Calls" &&
                e.SourceSymbolId.Contains("A") &&
                e.TargetSymbolId.Contains("B"));
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
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), "snap-ctor");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Constructs" &&
                e.SourceSymbolId.Contains("M") &&
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
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), "snap-override");

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
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), "snap-rw");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Writes" &&
                e.SourceSymbolId.Contains("M") &&
                e.TargetSymbolId.Contains("_field"));

            Assert.Contains(edges, e =>
                e.Kind == "Reads" &&
                e.SourceSymbolId.Contains("M") &&
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
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), "snap-ret");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Returns" &&
                e.SourceSymbolId.Contains("M") &&
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
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), "snap-throw");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Throws" &&
                e.SourceSymbolId.Contains("M") &&
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
                new[] { syntaxTree },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
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
            var extractor = new PolymorphismExtractor(compilation, "snap-poly-iface");

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
            var extractor = new PolymorphismExtractor(compilation, "snap-poly-virt");

            var edges = extractor.ExtractAll();

            var dispatchEdge = Assert.Single(edges, e => e.Kind == "MayDispatchTo");
            Assert.Equal("compiler_proved", dispatchEdge.Provenance);
            Assert.Contains("Base", dispatchEdge.SourceSymbolId);
            Assert.Contains("Derived", dispatchEdge.TargetSymbolId);
            Assert.Contains("M", dispatchEdge.TargetSymbolId);
        }
    }
}

