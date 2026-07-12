using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace RoslynIndexer.Storage
{
    public class SqliteIndexStore : IIndexStore
    {
        private string? _dbPath;
        private bool _isOpen;

        public SqliteIndexStore(string dbPath)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        }

        public bool IsOpen => _isOpen;

        public void Open(string dbPath)
        {
            _dbPath = dbPath;
            _isOpen = true;
        }

        public void Close()
        {
            _isOpen = false;
        }

        private void EnsureOpen()
        {
            if (!_isOpen || _dbPath == null)
                throw new InvalidOperationException("Index store is not open.");
        }

        public void RunMigrations()
        {
            EnsureOpen();
            new MigrationRunner(_dbPath!).RunMigrations();
        }

        public int GetCurrentSchemaVersion()
        {
            EnsureOpen();
            return new MigrationRunner(_dbPath!).GetCurrentSchemaVersion();
        }

        public void ValidateSchema(int expectedVersion)
        {
            var actual = GetCurrentSchemaVersion();
            if (actual != expectedVersion)
                throw new InvalidOperationException(
                    $"Schema version mismatch: expected {expectedVersion}, got {actual}.");
        }

        public void SaveWorkspace(WorkspaceId id, string gitRoot, string solutionPath, DateTime createdAtUtc)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT OR REPLACE INTO workspaces (workspace_id, git_root, solution_path)
                VALUES (@workspaceId, @gitRoot, @solutionPath);
            ";
            command.Parameters.AddWithValue("@workspaceId", id.Value);
            command.Parameters.AddWithValue("@gitRoot", gitRoot);
            command.Parameters.AddWithValue("@solutionPath", solutionPath);
            command.ExecuteNonQuery();
        }

        public void SaveSnapshot(SnapshotManifest manifest)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var transaction = connection.BeginTransaction();
            try
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;

                
                command.CommandText = @"
                    INSERT OR REPLACE INTO workspaces (workspace_id, git_root, solution_path)
                    VALUES (@workspaceId, @gitRoot, @solutionPath);
                ";
                command.Parameters.AddWithValue("@workspaceId", manifest.WorkspaceId);
                command.Parameters.AddWithValue("@gitRoot", manifest.GitRoot);
                command.Parameters.AddWithValue("@solutionPath", manifest.SolutionPath);
                command.ExecuteNonQuery();

                
                command.CommandText = @"
                    INSERT INTO snapshots (
                        snapshot_id, workspace_id, built_at_utc, sdk_version, compiler_version,
                        database_schema_version, output_schema_version, extractor_version,
                        tool_version, previous_snapshot_id
                    ) VALUES (
                        @snapshotId, @workspaceId, @builtAtUtc, @sdkVersion, @compilerVersion,
                        @databaseSchemaVersion, @outputSchemaVersion, @extractorVersion,
                        @toolVersion, @previousSnapshotId
                    );
                ";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@snapshotId", manifest.SnapshotId);
                command.Parameters.AddWithValue("@workspaceId", manifest.WorkspaceId);
                command.Parameters.AddWithValue("@builtAtUtc", manifest.CreatedAtUtc.ToString("O"));
                command.Parameters.AddWithValue("@sdkVersion", manifest.SdkVersion ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@compilerVersion", manifest.CompilerVersion ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@databaseSchemaVersion", (object)DBNull.Value);
                command.Parameters.AddWithValue("@outputSchemaVersion", (object)DBNull.Value);
                command.Parameters.AddWithValue("@extractorVersion", (object)DBNull.Value);
                command.Parameters.AddWithValue("@toolVersion", (object)DBNull.Value);
                command.Parameters.AddWithValue("@previousSnapshotId", (object)DBNull.Value);
                command.ExecuteNonQuery();

                
                if (manifest.Documents.Any())
                {
                    command.CommandText = @"
                        INSERT INTO projects (snapshot_id, name, target_framework)
                        VALUES (@snapshotId, @name, @targetFramework);
                    ";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@snapshotId", manifest.SnapshotId);
                    command.Parameters.AddWithValue("@name", "DefaultProject");
                    command.Parameters.AddWithValue("@targetFramework", (object)DBNull.Value);
                    command.ExecuteNonQuery();

                    
                }

                
                foreach (var doc in manifest.Documents)
                {
                    command.CommandText = @"
                        INSERT INTO documents (document_id, relative_path)
                        VALUES (@documentId, @relativePath);
                    ";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@documentId", doc.DocumentId);
                    command.Parameters.AddWithValue("@relativePath", doc.FilePath);
                    command.ExecuteNonQuery();

                    command.CommandText = @"
                        INSERT INTO document_versions (
                            document_version_id, document_id, content_hash, content, encoding, byte_count
                        ) VALUES (
                            @documentVersionId, @documentId, @contentHash, @content, @encoding, @byteCount
                        );
                    ";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@documentVersionId", doc.DocumentId);
                    command.Parameters.AddWithValue("@documentId", doc.DocumentId);
                    command.Parameters.AddWithValue("@contentHash", doc.ContentHash);
                    command.Parameters.AddWithValue("@content", (object)DBNull.Value);
                    command.Parameters.AddWithValue("@encoding", doc.Encoding);
                    command.Parameters.AddWithValue("@byteCount", (object)DBNull.Value);
                    command.ExecuteNonQuery();

                    command.CommandText = @"
                        INSERT INTO snapshot_documents (snapshot_id, document_version_id)
                        VALUES (@snapshotId, @documentVersionId);
                    ";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@snapshotId", manifest.SnapshotId);
                    command.Parameters.AddWithValue("@documentVersionId", doc.DocumentId);
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public SnapshotManifest? LoadLatestSnapshot(WorkspaceId workspaceId)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT s.snapshot_id, s.workspace_id, w.git_root, w.solution_path,
                       s.sdk_version, s.compiler_version, s.built_at_utc
                FROM snapshots s
                JOIN workspaces w ON w.workspace_id = s.workspace_id
                WHERE s.workspace_id = @workspaceId
                ORDER BY s.built_at_utc DESC
                LIMIT 1;
            ";
            command.Parameters.AddWithValue("@workspaceId", workspaceId.Value);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            var snapshotId = reader.GetString(0);
            var workspaceIdStr = reader.GetString(1);
            var gitRoot = reader.GetString(2);
            var solutionPath = reader.GetString(3);
            var sdkVersion = reader.IsDBNull(4) ? null : reader.GetString(4);
            var compilerVersion = reader.IsDBNull(5) ? null : reader.GetString(5);
            var builtAtUtc = DateTime.Parse(reader.GetString(6), null,
                System.Globalization.DateTimeStyles.RoundtripKind);

            
            var documents = new List<DocumentVersion>();
            using var docCommand = connection.CreateCommand();
            docCommand.CommandText = @"
                SELECT d.document_id, d.relative_path, dv.content_hash, dv.encoding
                FROM snapshot_documents sd
                JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
                JOIN documents d ON d.document_id = dv.document_id
                WHERE sd.snapshot_id = @snapshotId;
            ";
            docCommand.Parameters.AddWithValue("@snapshotId", snapshotId);

            using var docReader = docCommand.ExecuteReader();
            while (docReader.Read())
            {
                documents.Add(new DocumentVersion(
                    documentId: docReader.GetString(0),
                    filePath: docReader.GetString(1),
                    contentHash: docReader.GetString(2),
                    encoding: docReader.IsDBNull(3) ? "" : docReader.GetString(3),
                    lineStart: "",
                    createdAtUtc: DateTime.MinValue
                ));
            }

            return new SnapshotManifest(
                snapshotId: snapshotId,
                workspaceId: workspaceIdStr,
                gitRoot: gitRoot,
                solutionPath: solutionPath,
                sdkVersion: sdkVersion ?? "",
                compilerVersion: compilerVersion ?? "",
                createdAtUtc: builtAtUtc,
                documents: documents
            );
        }
    }
}
