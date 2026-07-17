using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage
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
                        INSERT INTO documents (document_id, relative_path, last_changed_snapshot_id)
                        VALUES (@documentId, @relativePath, @snapshotId)
                        ON CONFLICT(document_id) DO UPDATE SET
                            last_changed_snapshot_id = excluded.last_changed_snapshot_id;
                    ";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@documentId", doc.DocumentId);
                    command.Parameters.AddWithValue("@relativePath", doc.FilePath);
                    command.Parameters.AddWithValue("@snapshotId", manifest.SnapshotId);
                    command.ExecuteNonQuery();

                    command.CommandText = @"
                        INSERT OR IGNORE INTO document_versions (
                            document_version_id, document_id, content_hash, content, encoding, byte_count, line_starts
                        ) VALUES (
                            @documentVersionId, @documentId, @contentHash, @content, @encoding, @byteCount, @lineStarts
                        );
                    ";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@documentVersionId", doc.ContentHash);
                    command.Parameters.AddWithValue("@documentId", doc.DocumentId);
                    command.Parameters.AddWithValue("@contentHash", doc.ContentHash);
                    command.Parameters.AddWithValue("@content", (object?)(doc.Content) ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@encoding", string.IsNullOrEmpty(doc.Encoding) ? (object)DBNull.Value : (object)doc.Encoding);
                    command.Parameters.AddWithValue("@byteCount", doc.ByteCount > 0 ? (object)doc.ByteCount : (object)DBNull.Value);
                    command.Parameters.AddWithValue("@lineStarts", string.IsNullOrEmpty(doc.LineStarts) ? (object)DBNull.Value : (object)doc.LineStarts);
                    command.ExecuteNonQuery();

                    command.CommandText = @"
                        INSERT INTO snapshot_documents (snapshot_id, document_version_id)
                        VALUES (@snapshotId, @documentVersionId);
                    ";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@snapshotId", manifest.SnapshotId);
                    command.Parameters.AddWithValue("@documentVersionId", doc.ContentHash);
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

        public void MarkSnapshotInProgress(string snapshotId)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE snapshots SET status = 'in_progress' WHERE snapshot_id = @snapshotId;";
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            command.ExecuteNonQuery();
        }

        public void MarkSnapshotComplete(string snapshotId)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE snapshots SET status = 'complete' WHERE snapshot_id = @snapshotId;";
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            command.ExecuteNonQuery();
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
                  AND s.status = 'complete'
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
                SELECT d.document_id, d.relative_path, dv.content_hash, dv.encoding,
                       dv.line_starts
                FROM snapshot_documents sd
                JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
                JOIN documents d ON d.document_id = dv.document_id
                WHERE sd.snapshot_id = @snapshotId;
            ";
            docCommand.Parameters.AddWithValue("@snapshotId", snapshotId);

            using var docReader = docCommand.ExecuteReader();
            while (docReader.Read())
            {
                var lineStarts = docReader.IsDBNull(4) ? "" : docReader.GetString(4);
                documents.Add(new DocumentVersion(
                    documentId: docReader.GetString(0),
                    filePath: docReader.GetString(1),
                    contentHash: docReader.GetString(2),
                    encoding: docReader.IsDBNull(3) ? "" : docReader.GetString(3),
                    lineStart: lineStarts,
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

        public string? GetLatestSnapshotId(string? workspaceId = null)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            if (!string.IsNullOrEmpty(workspaceId))
            {
                command.CommandText = "SELECT snapshot_id FROM snapshots WHERE workspace_id = @workspaceId AND status = 'complete' ORDER BY built_at_utc DESC LIMIT 1;";
                command.Parameters.AddWithValue("@workspaceId", workspaceId);
            }
            else
            {
                command.CommandText = "SELECT snapshot_id FROM snapshots WHERE status = 'complete' ORDER BY built_at_utc DESC LIMIT 1;";
            }

            return command.ExecuteScalar() as string;
        }

        public string? GetSource(string relativePath, string snapshotId)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT dv.content
                FROM snapshot_documents sd
                JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
                JOIN documents d ON d.document_id = dv.document_id
                WHERE d.relative_path = @relativePath
                  AND sd.snapshot_id = @snapshotId;
            ";
            command.Parameters.AddWithValue("@relativePath", relativePath);
            command.Parameters.AddWithValue("@snapshotId", snapshotId);

            var result = command.ExecuteScalar();
            if (result == null || result == DBNull.Value)
                return null;

            var bytes = (byte[])result;
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        public void SaveDeclarations(string snapshotId, IEnumerable<SymbolDeclaration> declarations)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var transaction = connection.BeginTransaction();
            try
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;

                foreach (var decl in declarations)
                {
                    command.CommandText = @"
                        INSERT OR REPLACE INTO symbols (symbol_id, doc_comment_id, assembly_identity, kind, metadata_json, fqn)
                        VALUES (@symbolId, @docCommentId, @assemblyIdentity, @kind, @metadataJson, @fqn);
                    ";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@symbolId", decl.SymbolId.Value);
                    command.Parameters.AddWithValue("@docCommentId", decl.SymbolId.DocCommentId);
                    command.Parameters.AddWithValue("@assemblyIdentity", decl.SymbolId.AssemblyIdentity);
                    command.Parameters.AddWithValue("@kind", decl.Kind.ToString());
                    command.Parameters.AddWithValue("@metadataJson", (object?)decl.MetadataJson ?? DBNull.Value);
                    command.Parameters.AddWithValue("@fqn", (object?)decl.SymbolId.FullyQualifiedName ?? DBNull.Value);
                    command.ExecuteNonQuery();

                    command.CommandText = @"
                        INSERT INTO snapshot_symbols (snapshot_id, symbol_id, fqn, metadata_json)
                        VALUES (@snapshotId, @symbolId, @fqn, @metadataJson)
                        ON CONFLICT(snapshot_id, symbol_id) DO UPDATE SET
                            fqn = excluded.fqn,
                            metadata_json = excluded.metadata_json;
                    ";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@snapshotId", snapshotId);
                    command.Parameters.AddWithValue("@symbolId", decl.SymbolId.Value);
                    command.Parameters.AddWithValue("@fqn", (object?)decl.SymbolId.FullyQualifiedName ?? DBNull.Value);
                    command.Parameters.AddWithValue("@metadataJson", (object?)decl.MetadataJson ?? DBNull.Value);
                    command.ExecuteNonQuery();

                    command.CommandText = @"
                        INSERT INTO declarations (
                            symbol_id, document_version_id,
                            full_start, full_end,
                            signature_start, signature_end,
                            body_start, body_end,
                            name_start, name_end,
                            is_partial,
                            is_generated,
                            generator_identity
                        ) VALUES (
                            @symbolId, @documentVersionId,
                            @fullStart, @fullEnd,
                            @signatureStart, @signatureEnd,
                            @bodyStart, @bodyEnd,
                            @nameStart, @nameEnd,
                            @isPartial,
                            @isGenerated,
                            @generatorIdentity
                        )
                        ON CONFLICT(symbol_id, document_version_id)
                        DO UPDATE SET
                            full_start        = excluded.full_start,
                            full_end          = excluded.full_end,
                            signature_start   = excluded.signature_start,
                            signature_end     = excluded.signature_end,
                            body_start        = excluded.body_start,
                            body_end          = excluded.body_end,
                            name_start        = excluded.name_start,
                            name_end          = excluded.name_end,
                            is_partial        = excluded.is_partial,
                            is_generated      = excluded.is_generated,
                            generator_identity = excluded.generator_identity;
                    ";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@symbolId", decl.SymbolId.Value);
                    command.Parameters.AddWithValue("@documentVersionId", decl.DocumentVersionId);
                    command.Parameters.AddWithValue("@fullStart", (object?)decl.FullSpan.Start ?? DBNull.Value);
                    command.Parameters.AddWithValue("@fullEnd", (object?)decl.FullSpan.End ?? DBNull.Value);
                    command.Parameters.AddWithValue("@signatureStart", (object?)decl.SignatureSpan.Start ?? DBNull.Value);
                    command.Parameters.AddWithValue("@signatureEnd", (object?)decl.SignatureSpan.End ?? DBNull.Value);
                    command.Parameters.AddWithValue("@bodyStart", (object?)decl.BodySpan.Start ?? DBNull.Value);
                    command.Parameters.AddWithValue("@bodyEnd", (object?)decl.BodySpan.End ?? DBNull.Value);
                    command.Parameters.AddWithValue("@nameStart", (object?)decl.NameSpan.Start ?? DBNull.Value);
                    command.Parameters.AddWithValue("@nameEnd", (object?)decl.NameSpan.End ?? DBNull.Value);
                    command.Parameters.AddWithValue("@isPartial", decl.IsPartial ? 1 : 0);
                    command.Parameters.AddWithValue("@isGenerated", decl.IsGenerated ? 1 : 0);
                    command.Parameters.AddWithValue("@generatorIdentity", (object?)decl.GeneratorIdentity ?? DBNull.Value);
                    command.ExecuteNonQuery();

                    if (decl.IsPartial)
                    {
                        command.CommandText = @"
                            INSERT OR IGNORE INTO partial_declarations (symbol_id, declaration_id)
                            SELECT @symbolId, declaration_id
                            FROM declarations
                            WHERE symbol_id = @symbolId AND document_version_id = @documentVersionId;
                        ";
                        command.Parameters.Clear();
                        command.Parameters.AddWithValue("@symbolId", decl.SymbolId.Value);
                        command.Parameters.AddWithValue("@documentVersionId", decl.DocumentVersionId);
                        command.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public SymbolInfo? GetSymbolInfo(string symbolId, string snapshotId)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT s.symbol_id, s.doc_comment_id, s.assembly_identity, s.kind, ss.fqn, ss.metadata_json,
                       (SELECT COUNT(*) FROM declarations d WHERE d.symbol_id = s.symbol_id) AS decl_count,
                       (SELECT MAX(d.is_partial) FROM declarations d WHERE d.symbol_id = s.symbol_id) AS is_partial
                FROM symbols s
                JOIN snapshot_symbols ss ON ss.symbol_id = s.symbol_id
                WHERE s.symbol_id = @symbolId AND ss.snapshot_id = @snapshotId;
            ";
            command.Parameters.AddWithValue("@symbolId", symbolId);
            command.Parameters.AddWithValue("@snapshotId", snapshotId);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            var sid = new SymbolId(
                docCommentId: reader.GetString(1),
                assemblyIdentity: reader.GetString(2),
                fullyQualifiedName: reader.IsDBNull(4) ? null : reader.GetString(4));

            var kindStr = reader.GetString(3);
            Enum.TryParse<SymbolKind>(kindStr, ignoreCase: true, out var kind);

            return new SymbolInfo(
                symbolId: sid,
                kind: kind,
                fullyQualifiedName: reader.IsDBNull(4) ? null : reader.GetString(4),
                metadataJson: reader.IsDBNull(5) ? null : reader.GetString(5),
                declarationCount: reader.GetInt32(6),
                isPartial: reader.GetInt32(7) == 1);
        }

        public string? GetSymbolSource(string symbolId, string snapshotId, ViewKind viewKind, bool includeGenerated = false)
        {
            string startCol, endCol;
            switch (viewKind)
            {
                case ViewKind.Declaration:
                    startCol = "full_start"; endCol = "full_end"; break;
                case ViewKind.Signature:
                    startCol = "signature_start"; endCol = "signature_end"; break;
                case ViewKind.Body:
                    startCol = "body_start"; endCol = "body_end"; break;
                case ViewKind.Name:
                    startCol = "name_start"; endCol = "name_end"; break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(viewKind), viewKind,
                        "Use GetContainingTypeSource or GetSurroundingLines for this view kind.");
            }

            var (content, start, end) = GetSymbolSpanContent(symbolId, snapshotId, startCol, endCol, includeGenerated);
            if (content == null || start == null || end == null)
                return null;

            return SliceToString(content, start.Value, end.Value);
        }

        public string? GetContainingTypeSource(string symbolId, string snapshotId)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT s.doc_comment_id, s.assembly_identity
                FROM symbols s
                JOIN snapshot_symbols ss ON ss.symbol_id = s.symbol_id
                WHERE s.symbol_id = @symbolId AND ss.snapshot_id = @snapshotId;
            ";
            command.Parameters.AddWithValue("@symbolId", symbolId);
            command.Parameters.AddWithValue("@snapshotId", snapshotId);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            var docCommentId = reader.GetString(0);
            var assemblyIdentity = reader.GetString(1);

            var parentDocCommentId = DeriveParentTypeDocCommentId(docCommentId);
            if (parentDocCommentId == null)
                return null;

            var parentSymbolId = $"{parentDocCommentId}|{assemblyIdentity}";

            return GetSymbolSource(parentSymbolId, snapshotId, ViewKind.Declaration);
        }

        public string? GetSurroundingLines(string symbolId, string snapshotId, int contextLines)
        {
            var (content, fullStart, fullEnd) = GetSymbolSpanContent(symbolId, snapshotId, "full_start", "full_end");
            if (content == null || fullStart == null || fullEnd == null)
                return null;

            var lineStarts = GetLineStarts(symbolId, snapshotId);
            if (lineStarts == null || lineStarts.Length == 0)
                return null;

            int startLine = FindLineIndex(lineStarts, fullStart.Value);
            int endLine = FindLineIndex(lineStarts, fullEnd.Value - 1);

            int expandedStartLine = Math.Max(0, startLine - contextLines);
            int expandedEndLine = Math.Min(lineStarts.Length - 1, endLine + contextLines);

            int byteStart = lineStarts[expandedStartLine];
            int byteEnd;
            if (expandedEndLine + 1 < lineStarts.Length)
                byteEnd = lineStarts[expandedEndLine + 1];
            else
                byteEnd = content.Length;

            return SliceToString(content, byteStart, byteEnd);
        }

        private (byte[]? Content, int? Start, int? End) GetSymbolSpanContent(
            string symbolId, string snapshotId, string startCol, string endCol, bool includeGenerated = false)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = $@"
                SELECT dv.content, d.{startCol}, d.{endCol}
                FROM snapshot_symbols ss
                JOIN declarations d ON d.symbol_id = ss.symbol_id
                JOIN document_versions dv ON dv.document_version_id = d.document_version_id
                WHERE ss.snapshot_id = @snapshotId
                  AND ss.symbol_id = @symbolId
            ";

            if (!includeGenerated)
            {
                command.CommandText += " AND (d.is_generated = 0 OR d.is_generated IS NULL)";
            }

            command.CommandText += " LIMIT 1;";

            command.Parameters.AddWithValue("@symbolId", symbolId);
            command.Parameters.AddWithValue("@snapshotId", snapshotId);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return (null, null, null);

            var content = reader.IsDBNull(0) ? null : (byte[])reader[0];
            var start = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
            var end = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);

            return (content, start, end);
        }

        private int[]? GetLineStarts(string symbolId, string snapshotId)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT dv.line_starts
                FROM snapshot_symbols ss
                JOIN declarations d ON d.symbol_id = ss.symbol_id
                JOIN document_versions dv ON dv.document_version_id = d.document_version_id
                WHERE ss.snapshot_id = @snapshotId
                  AND ss.symbol_id = @symbolId
                LIMIT 1;
            ";
            command.Parameters.AddWithValue("@symbolId", symbolId);
            command.Parameters.AddWithValue("@snapshotId", snapshotId);

            var result = command.ExecuteScalar();
            if (result == null || result == DBNull.Value)
                return null;

            var json = (string)result;
            return JsonSerializer.Deserialize<int[]>(json);
        }

        private static int FindLineIndex(int[] lineStarts, int byteOffset)
        {
            int lo = 0, hi = lineStarts.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (lineStarts[mid] <= byteOffset)
                    lo = mid;
                else
                    hi = mid - 1;
            }
            return lo;
        }

        private static string? SliceToString(byte[] content, int start, int end)
        {
            if (start < 0 || end > content.Length || start > end)
                return null;
            int length = end - start;
            if (length == 0)
                return string.Empty;
            return Encoding.UTF8.GetString(content, start, length);
        }

        public void BuildSearchIndex(string snapshotId)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var transaction = connection.BeginTransaction();
            try
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;

                command.CommandText = "DELETE FROM source_fts WHERE snapshot_id = @snapshotId;";
                command.Parameters.AddWithValue("@snapshotId", snapshotId);
                command.ExecuteNonQuery();

                command.CommandText = "DELETE FROM symbol_fts WHERE snapshot_id = @snapshotId;";
                command.ExecuteNonQuery();

                command.CommandText = @"
                    INSERT INTO source_fts (document_path, content, snapshot_id, document_version_id)
                    SELECT d.relative_path, CAST(dv.content AS TEXT), sd.snapshot_id, dv.document_version_id
                    FROM snapshot_documents sd
                    JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
                    JOIN documents d ON d.document_id = dv.document_id
                    WHERE sd.snapshot_id = @snapshotId
                      AND dv.content IS NOT NULL;
                ";
                command.ExecuteNonQuery();

                command.CommandText = @"
                    INSERT INTO symbol_fts (symbol_id, fqn, doc_comment_id, kind, snapshot_id)
                    SELECT s.symbol_id, ss.fqn, s.doc_comment_id, s.kind, ss.snapshot_id
                    FROM snapshot_symbols ss
                    JOIN symbols s ON s.symbol_id = ss.symbol_id
                    WHERE ss.snapshot_id = @snapshotId
                      AND ss.fqn IS NOT NULL;
                ";
                command.ExecuteNonQuery();

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public List<SourceSearchResult> SearchSource(string query, string snapshotId, int limit = 20, bool includeGenerated = false)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();

            command.CommandText = @"
                SELECT document_path,
                       highlight(source_fts, 1, '<mark>', '</mark>') AS snippet
                FROM source_fts
                JOIN documents d ON source_fts.document_path = d.relative_path
                JOIN document_versions dv ON dv.document_id = d.document_id
                LEFT JOIN declarations dec ON dec.document_version_id = dv.document_version_id
                WHERE source_fts MATCH @query
                  AND source_fts.snapshot_id = @snapshotId
            ";

            if (!includeGenerated)
            {
                command.CommandText += " AND (dec.is_generated IS NULL OR dec.is_generated = 0)";
            }

            command.CommandText += @"
                ORDER BY rank
                LIMIT @limit;
            ";

            command.Parameters.AddWithValue("@query", query);
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            command.Parameters.AddWithValue("@limit", limit);

            var results = new List<SourceSearchResult>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new SourceSearchResult(
                    documentPath: reader.GetString(0),
                    snippet: reader.IsDBNull(1) ? "" : reader.GetString(1)));
            }
            return results;
        }

        public List<SymbolSearchResult> SearchSymbols(string query, string snapshotId, int limit = 20, bool includeGenerated = false, string? kind = null)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT symbol_fts.symbol_id, fqn, doc_comment_id, kind
                FROM symbol_fts
                LEFT JOIN declarations dec ON symbol_fts.symbol_id = dec.symbol_id
                WHERE symbol_fts MATCH @query
                  AND symbol_fts.snapshot_id = @snapshotId
            ";

            if (!string.IsNullOrEmpty(kind))
            {
                command.CommandText += " AND symbol_fts.kind = @kind";
                command.Parameters.AddWithValue("@kind", kind);
            }

            if (!includeGenerated)
            {
                command.CommandText += " AND (dec.is_generated IS NULL OR dec.is_generated = 0)";
            }

            command.CommandText += @"
                ORDER BY rank
                LIMIT @limit;
            ";

            command.Parameters.AddWithValue("@query", query);
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            command.Parameters.AddWithValue("@limit", limit);

            var results = new List<SymbolSearchResult>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new SymbolSearchResult(
                    symbolId: reader.GetString(0),
                    fullyQualifiedName: reader.IsDBNull(1) ? "" : reader.GetString(1),
                    kind: reader.GetString(3),
                    docCommentId: reader.GetString(2)));
            }
            return results;
        }

        public SymbolInfo? ResolveSymbolByFqn(string fqn, string snapshotId, bool includeGenerated = false)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();

            command.CommandText = @"
                SELECT s.symbol_id, s.doc_comment_id, s.assembly_identity, s.kind, ss.fqn, ss.metadata_json,
                       (SELECT COUNT(*) FROM declarations d WHERE d.symbol_id = s.symbol_id) AS decl_count,
                       (SELECT MAX(d.is_partial) FROM declarations d WHERE d.symbol_id = s.symbol_id) AS is_partial
                FROM symbols s
                JOIN snapshot_symbols ss ON ss.symbol_id = s.symbol_id
                JOIN declarations d ON d.symbol_id = s.symbol_id
                WHERE ss.fqn = @fqn AND ss.snapshot_id = @snapshotId
            ";

            if (!includeGenerated)
            {
                command.CommandText += " AND (d.is_generated = 0 OR d.is_generated IS NULL)";
            }

            command.CommandText += " LIMIT 1;";

            command.Parameters.AddWithValue("@fqn", fqn);
            command.Parameters.AddWithValue("@snapshotId", snapshotId);

            using var reader = command.ExecuteReader();
            if (reader.Read())
                return ReadSymbolInfo(reader);

            reader.Close();
            command.Parameters.Clear();
            command.CommandText = @"
                SELECT s.symbol_id, s.doc_comment_id, s.assembly_identity, s.kind, ss.fqn, ss.metadata_json,
                       (SELECT COUNT(*) FROM declarations d WHERE d.symbol_id = s.symbol_id) AS decl_count,
                       (SELECT MAX(d.is_partial) FROM declarations d WHERE d.symbol_id = s.symbol_id) AS is_partial
                FROM symbols s
                JOIN snapshot_symbols ss ON ss.symbol_id = s.symbol_id
                JOIN declarations d ON d.symbol_id = s.symbol_id
                WHERE ss.fqn LIKE @pattern AND ss.snapshot_id = @snapshotId
            ";

            if (!includeGenerated)
            {
                command.CommandText += " AND (d.is_generated = 0 OR d.is_generated IS NULL)";
            }

            command.CommandText += " LIMIT 1;";

            command.Parameters.AddWithValue("@pattern", $"{fqn}%");
            command.Parameters.AddWithValue("@snapshotId", snapshotId);

            using var reader2 = command.ExecuteReader();
            if (reader2.Read())
                return ReadSymbolInfo(reader2);

            return null;
        }

        private static SymbolInfo? ReadSymbolInfo(SqliteDataReader reader)
        {
            var sid = new SymbolId(
                docCommentId: reader.GetString(1),
                assemblyIdentity: reader.GetString(2),
                fullyQualifiedName: reader.IsDBNull(4) ? null : reader.GetString(4));

            var kindStr = reader.GetString(3);
            Enum.TryParse<SymbolKind>(kindStr, ignoreCase: true, out var kind);

            return new SymbolInfo(
                symbolId: sid,
                kind: kind,
                fullyQualifiedName: reader.IsDBNull(4) ? null : reader.GetString(4),
                metadataJson: reader.IsDBNull(5) ? null : reader.GetString(5),
                declarationCount: reader.GetInt32(6),
                isPartial: reader.GetInt32(7) == 1);
        }

        public void SaveEdges(string snapshotId, IEnumerable<EdgeRecord> edges)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var transaction = connection.BeginTransaction();
            try
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;

                foreach (var edge in edges)
                {
                    command.CommandText = @"
                        INSERT INTO edges (
                            snapshot_id, source_symbol_id, target_symbol_id, kind, provenance,
                            extractor_version, source_document_path,
                            source_start_line, source_start_column,
                            source_end_line, source_end_column
                        ) VALUES (
                            @snapshotId, @sourceSymbolId, @targetSymbolId, @kind, @provenance,
                            @extractorVersion, @sourceDocumentPath,
                            @sourceStartLine, @sourceStartColumn,
                            @sourceEndLine, @sourceEndColumn
                        );
                    ";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@snapshotId", snapshotId);
                    command.Parameters.AddWithValue("@sourceSymbolId", edge.SourceSymbolId);
                    command.Parameters.AddWithValue("@targetSymbolId", edge.TargetSymbolId);
                    command.Parameters.AddWithValue("@kind", edge.Kind);
                    command.Parameters.AddWithValue("@provenance", (object?)edge.Provenance ?? DBNull.Value);
                    command.Parameters.AddWithValue("@extractorVersion", (object?)edge.ExtractorVersion ?? DBNull.Value);
                    command.Parameters.AddWithValue("@sourceDocumentPath", (object?)edge.SourceDocumentPath ?? DBNull.Value);
                    command.Parameters.AddWithValue("@sourceStartLine", (object?)edge.SourceStartLine ?? DBNull.Value);
                    command.Parameters.AddWithValue("@sourceStartColumn", (object?)edge.SourceStartColumn ?? DBNull.Value);
                    command.Parameters.AddWithValue("@sourceEndLine", (object?)edge.SourceEndLine ?? DBNull.Value);
                    command.Parameters.AddWithValue("@sourceEndColumn", (object?)edge.SourceEndColumn ?? DBNull.Value);
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

        public List<EdgeRecord> GetEdges(string snapshotId, string? symbolId = null)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            if (symbolId != null)
            {
                command.CommandText = @"
                    SELECT source_symbol_id, target_symbol_id, kind, provenance,
                           snapshot_id, extractor_version,
                           source_document_path, source_start_line, source_start_column,
                           source_end_line, source_end_column
                    FROM edges
                    WHERE snapshot_id = @snapshotId
                      AND (source_symbol_id = @symbolId OR target_symbol_id = @symbolId)
                    ORDER BY edge_id;
                ";
                command.Parameters.AddWithValue("@symbolId", symbolId);
            }
            else
            {
                command.CommandText = @"
                    SELECT source_symbol_id, target_symbol_id, kind, provenance,
                           snapshot_id, extractor_version,
                           source_document_path, source_start_line, source_start_column,
                           source_end_line, source_end_column
                    FROM edges
                    WHERE snapshot_id = @snapshotId
                    ORDER BY edge_id;
                ";
            }
            command.Parameters.AddWithValue("@snapshotId", snapshotId);

            return ReadEdgeRecords(command);
        }

        public List<EdgeRecord> GetEdgesByKind(string snapshotId, string kind)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT source_symbol_id, target_symbol_id, kind, provenance,
                       snapshot_id, extractor_version,
                       source_document_path, source_start_line, source_start_column,
                       source_end_line, source_end_column
                FROM edges
                WHERE snapshot_id = @snapshotId AND kind = @kind
                ORDER BY edge_id;
            ";
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            command.Parameters.AddWithValue("@kind", kind);

            return ReadEdgeRecords(command);
        }

        public List<EdgeRecord> GetIncomingEdges(string snapshotId, string symbolId)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT source_symbol_id, target_symbol_id, kind, provenance,
                       snapshot_id, extractor_version,
                       source_document_path, source_start_line, source_start_column,
                       source_end_line, source_end_column
                FROM edges
                WHERE snapshot_id = @snapshotId AND target_symbol_id = @symbolId
                ORDER BY edge_id;
            ";
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            command.Parameters.AddWithValue("@symbolId", symbolId);

            return ReadEdgeRecords(command);
        }

        public List<EdgeRecord> GetOutgoingEdges(string snapshotId, string symbolId)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT source_symbol_id, target_symbol_id, kind, provenance,
                       snapshot_id, extractor_version,
                       source_document_path, source_start_line, source_start_column,
                       source_end_line, source_end_column
                FROM edges
                WHERE snapshot_id = @snapshotId AND source_symbol_id = @symbolId
                ORDER BY edge_id;
            ";
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            command.Parameters.AddWithValue("@symbolId", symbolId);

            return ReadEdgeRecords(command);
        }

        private static List<EdgeRecord> ReadEdgeRecords(SqliteCommand command)
        {
            var results = new List<EdgeRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new EdgeRecord(
                    sourceSymbolId: reader.GetString(0),
                    targetSymbolId: reader.GetString(1),
                    kind: reader.GetString(2),
                    provenance: reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    snapshotId: reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    extractorVersion: reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    sourceDocumentPath: reader.IsDBNull(6) ? null : reader.GetString(6),
                    sourceStartLine: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    sourceStartColumn: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    sourceEndLine: reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    sourceEndColumn: reader.IsDBNull(10) ? null : reader.GetInt32(10)));
            }
            return results;
        }

        public void SaveDiagnostics(string snapshotId, IEnumerable<DiagnosticRecord> diagnostics)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var transaction = connection.BeginTransaction();
            try
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;

                foreach (var diag in diagnostics)
                {
                    command.CommandText = @"
                        INSERT INTO diagnostics (snapshot_id, project_name, document_path, severity, id, message,
                                                 start_line, start_column, end_line, end_column)
                        VALUES (@snapshotId, @projectName, @documentPath, @severity, @id, @message,
                                @startLine, @startColumn, @endLine, @endColumn);
                    ";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@snapshotId", snapshotId);
                    command.Parameters.AddWithValue("@projectName", diag.ProjectName);
                    command.Parameters.AddWithValue("@documentPath", (object?)diag.DocumentPath ?? DBNull.Value);
                    command.Parameters.AddWithValue("@severity", diag.Severity);
                    command.Parameters.AddWithValue("@id", diag.Id);
                    command.Parameters.AddWithValue("@message", diag.Message);
                    command.Parameters.AddWithValue("@startLine", (object?)diag.StartLine ?? DBNull.Value);
                    command.Parameters.AddWithValue("@startColumn", (object?)diag.StartColumn ?? DBNull.Value);
                    command.Parameters.AddWithValue("@endLine", (object?)diag.EndLine ?? DBNull.Value);
                    command.Parameters.AddWithValue("@endColumn", (object?)diag.EndColumn ?? DBNull.Value);
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

        public List<DiagnosticRecord> GetDiagnostics(string snapshotId, string? projectName = null)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            if (projectName != null)
            {
                command.CommandText = @"
                    SELECT project_name, document_path, severity, id, message,
                           start_line, start_column, end_line, end_column
                    FROM diagnostics
                    WHERE snapshot_id = @snapshotId AND project_name = @projectName
                    ORDER BY diagnostic_id;
                ";
                command.Parameters.AddWithValue("@projectName", projectName);
            }
            else
            {
                command.CommandText = @"
                    SELECT project_name, document_path, severity, id, message,
                           start_line, start_column, end_line, end_column
                    FROM diagnostics
                    WHERE snapshot_id = @snapshotId
                    ORDER BY diagnostic_id;
                ";
            }
            command.Parameters.AddWithValue("@snapshotId", snapshotId);

            var results = new List<DiagnosticRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new DiagnosticRecord(
                    projectName: reader.GetString(0),
                    documentPath: reader.IsDBNull(1) ? null : reader.GetString(1),
                    severity: reader.GetString(2),
                    id: reader.GetString(3),
                    message: reader.GetString(4),
                    startLine: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    startColumn: reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    endLine: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    endColumn: reader.IsDBNull(8) ? null : reader.GetInt32(8)));
            }
            return results;
        }

        public void SaveAnnotations(string snapshotId, IEnumerable<AnnotationRecord> annotations)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var transaction = connection.BeginTransaction();
            try
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;

                foreach (var ann in annotations)
                {
                    command.CommandText = @"
                        INSERT INTO annotations (snapshot_id, symbol_id, kind, value)
                        VALUES (@snapshotId, @symbolId, @kind, @value);
                    ";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@snapshotId", snapshotId);
                    command.Parameters.AddWithValue("@symbolId", ann.SymbolId);
                    command.Parameters.AddWithValue("@kind", ann.Kind);
                    command.Parameters.AddWithValue("@value", ann.Value);
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

        public List<AnnotationRecord> GetAnnotations(string snapshotId, string? symbolId = null)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            if (symbolId != null)
            {
                command.CommandText = @"
                    SELECT symbol_id, kind, value
                    FROM annotations
                    WHERE snapshot_id = @snapshotId AND symbol_id = @symbolId
                    ORDER BY annotation_id;
                ";
                command.Parameters.AddWithValue("@symbolId", symbolId);
            }
            else
            {
                command.CommandText = @"
                    SELECT symbol_id, kind, value
                    FROM annotations
                    WHERE snapshot_id = @snapshotId
                    ORDER BY annotation_id;
                ";
            }
            command.Parameters.AddWithValue("@snapshotId", snapshotId);

            var results = new List<AnnotationRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new AnnotationRecord(
                    symbolId: reader.GetString(0),
                    kind: reader.GetString(1),
                    value: reader.GetString(2)));
            }
            return results;
        }

        public void DeleteEdgesByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths)
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
                    DELETE FROM edges
                    WHERE snapshot_id = @snapshotId
                      AND source_document_path IN (" + string.Join(", ", documentPaths.Select((_, i) => $"@p{i}")) + @");
                ";
                command.Parameters.AddWithValue("@snapshotId", snapshotId);
                int i = 0;
                foreach (var path in documentPaths)
                    command.Parameters.AddWithValue($"@p{i++}", path);
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public void DeleteEdgesWithNullDocumentPath(string snapshotId)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM edges WHERE snapshot_id = @snapshotId AND source_document_path IS NULL;";
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            command.ExecuteNonQuery();
        }

        public void DeleteDeclarationsByDocumentVersionIds(IEnumerable<string> documentVersionIds)
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
                    DELETE FROM declarations
                    WHERE document_version_id IN (" + string.Join(", ", documentVersionIds.Select((_, i) => $"@p{i}")) + @");
                ";
                int i = 0;
                foreach (var id in documentVersionIds)
                    command.Parameters.AddWithValue($"@p{i++}", id);
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public void CopyEdgesToSnapshot(string fromSnapshotId, string toSnapshotId)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO edges (
                    snapshot_id, source_symbol_id, target_symbol_id, kind, provenance,
                    extractor_version, source_document_path,
                    source_start_line, source_start_column,
                    source_end_line, source_end_column
                )
                SELECT @toSnapshotId, source_symbol_id, target_symbol_id, kind, provenance,
                       extractor_version, source_document_path,
                       source_start_line, source_start_column,
                       source_end_line, source_end_column
                FROM edges
                WHERE snapshot_id = @fromSnapshotId;
            ";
            command.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
            command.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);
            command.ExecuteNonQuery();
        }

        public void CopySnapshotSymbols(string fromSnapshotId, string toSnapshotId)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT OR REPLACE INTO snapshot_symbols (snapshot_id, symbol_id, fqn, metadata_json)
                SELECT @toSnapshotId, symbol_id, fqn, metadata_json
                FROM snapshot_symbols
                WHERE snapshot_id = @fromSnapshotId;
            ";
            command.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
            command.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);
            command.ExecuteNonQuery();
        }

        public Dictionary<string, string> GetDocumentVersionIdsByPath(string snapshotId)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT d.relative_path, dv.document_version_id
                FROM snapshot_documents sd
                JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
                JOIN documents d ON d.document_id = dv.document_id
                WHERE sd.snapshot_id = @snapshotId;
            ";
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            var result = new Dictionary<string, string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = reader.GetString(1);
            return result;
        }

        public List<string> GetDocumentVersionIdsForDocuments(string snapshotId, IEnumerable<string> documentPaths)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT dv.document_version_id
                FROM snapshot_documents sd
                JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
                JOIN documents d ON d.document_id = dv.document_id
                WHERE sd.snapshot_id = @snapshotId
                  AND d.relative_path IN (" + string.Join(", ", documentPaths.Select((_, i) => $"@p{i}")) + @");
            ";
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            int i = 0;
            foreach (var path in documentPaths)
                command.Parameters.AddWithValue($"@p{i++}", path);
            var results = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                results.Add(reader.GetString(0));
            return results;
        }

        public void DeleteSnapshotSymbolsBySymbolIds(string snapshotId, IEnumerable<string> symbolIds)
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
                    DELETE FROM snapshot_symbols
                    WHERE snapshot_id = @snapshotId
                      AND symbol_id IN (" + string.Join(", ", symbolIds.Select((_, i) => $"@p{i}")) + @");
                ";
                command.Parameters.AddWithValue("@snapshotId", snapshotId);
                int i = 0;
                foreach (var id in symbolIds)
                    command.Parameters.AddWithValue($"@p{i++}", id);
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public List<string> GetSymbolIdsByDocumentVersionIds(string snapshotId, IEnumerable<string> documentVersionIds)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT DISTINCT ss.symbol_id
                FROM snapshot_symbols ss
                JOIN declarations d ON d.symbol_id = ss.symbol_id
                WHERE ss.snapshot_id = @snapshotId
                  AND d.document_version_id IN (" + string.Join(", ", documentVersionIds.Select((_, i) => $"@p{i}")) + @");
            ";
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            int i = 0;
            foreach (var id in documentVersionIds)
                command.Parameters.AddWithValue($"@p{i++}", id);
            var results = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                results.Add(reader.GetString(0));
            return results;
        }

        public void SaveSnapshotDocuments(string snapshotId, IEnumerable<(string DocumentId, string DocumentVersionId)> entries)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var transaction = connection.BeginTransaction();
            try
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                foreach (var (docId, versionId) in entries)
                {
                    command.CommandText = @"
                        INSERT OR REPLACE INTO snapshot_documents (snapshot_id, document_version_id)
                        VALUES (@snapshotId, @documentVersionId);
                    ";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@snapshotId", snapshotId);
                    command.Parameters.AddWithValue("@documentVersionId", versionId);
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

        public void SaveSnapshotSymbols(string snapshotId, IEnumerable<string> symbolIds)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var transaction = connection.BeginTransaction();
            try
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                foreach (var symbolId in symbolIds)
                {
                    command.CommandText = @"
                        INSERT OR REPLACE INTO snapshot_symbols (snapshot_id, symbol_id, fqn, metadata_json)
                        SELECT @snapshotId, @symbolId, fqn, metadata_json
                        FROM snapshot_symbols
                        WHERE symbol_id = @symbolId
                        LIMIT 1;
                    ";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@snapshotId", snapshotId);
                    command.Parameters.AddWithValue("@symbolId", symbolId);
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

        public void SaveSemanticChanges(string fromSnapshotId, string toSnapshotId, IEnumerable<SemanticChange> changes)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var transaction = connection.BeginTransaction();
            try
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;

                foreach (var change in changes)
                {
                    command.CommandText = @"
                        INSERT OR REPLACE INTO semantic_changes (
                            change_id, from_snapshot_id, to_snapshot_id,
                            change_type, symbol_id, detail_json, created_at_utc
                        ) VALUES (
                            @changeId, @fromSnapshotId, @toSnapshotId,
                            @changeType, @symbolId, @detailJson, @createdAtUtc
                        );
                    ";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@changeId", change.ChangeId);
                    command.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
                    command.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);
                    command.Parameters.AddWithValue("@changeType", change.ChangeType);
                    command.Parameters.AddWithValue("@symbolId", change.SymbolId);
                    command.Parameters.AddWithValue("@detailJson", (object?)change.DetailJson ?? DBNull.Value);
                    command.Parameters.AddWithValue("@createdAtUtc", change.CreatedAtUtc.ToString("O"));
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

        public List<SemanticChange> GetSemanticChanges(string fromSnapshotId, string toSnapshotId)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT change_id, from_snapshot_id, to_snapshot_id,
                       change_type, symbol_id, detail_json, created_at_utc
                FROM semantic_changes
                WHERE from_snapshot_id = @fromSnapshotId AND to_snapshot_id = @toSnapshotId
                ORDER BY created_at_utc;
            ";
            command.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
            command.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);

            var results = new List<SemanticChange>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new SemanticChange(
                    changeId: reader.GetString(0),
                    fromSnapshotId: reader.GetString(1),
                    toSnapshotId: reader.GetString(2),
                    changeType: reader.GetString(3),
                    symbolId: reader.GetString(4),
                    detailJson: reader.IsDBNull(5) ? null : reader.GetString(5),
                    createdAtUtc: DateTime.Parse(reader.GetString(6), null,
                        System.Globalization.DateTimeStyles.RoundtripKind)));
            }
            return results;
        }

        public List<string> GetSnapshotIds(string workspaceId)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT snapshot_id
                FROM snapshots
                WHERE workspace_id = @workspaceId
                ORDER BY built_at_utc;
            ";
            command.Parameters.AddWithValue("@workspaceId", workspaceId);

            var results = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(reader.GetString(0));
            }
            return results;
        }

        public List<string> GetSymbolIdsInSnapshot(string snapshotId)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT symbol_id
                FROM snapshot_symbols
                WHERE snapshot_id = @snapshotId
                ORDER BY symbol_id;
            ";
            command.Parameters.AddWithValue("@snapshotId", snapshotId);

            var results = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(reader.GetString(0));
            }
            return results;
        }

        public string? ResolveSymbolByLocation(string relativePath, int line, string snapshotId, bool includeGenerated = false)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var getDocCmd = connection.CreateCommand();
            getDocCmd.CommandText = @"
                SELECT dv.document_version_id, dv.line_starts
                FROM snapshot_documents sd
                JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
                JOIN documents doc ON doc.document_id = dv.document_id
                WHERE sd.snapshot_id = @snapshotId AND doc.relative_path = @relativePath
                LIMIT 1;
            ";
            getDocCmd.Parameters.AddWithValue("@snapshotId", snapshotId);
            getDocCmd.Parameters.AddWithValue("@relativePath", relativePath);

            string? docVersionId;
            string? lineStartsJson;
            using (var reader = getDocCmd.ExecuteReader())
            {
                if (!reader.Read())
                    return null;
                docVersionId = reader.GetString(0);
                lineStartsJson = reader.IsDBNull(1) ? null : reader.GetString(1);
            }

            if (lineStartsJson == null)
                return null;

            var lineStarts = JsonSerializer.Deserialize<int[]>(lineStartsJson);
            if (lineStarts == null || lineStarts.Length == 0)
                return null;

            int lineIndex = Math.Max(0, line - 1);
            if (lineIndex >= lineStarts.Length)
                return null;

            int byteOffset = lineStarts[lineIndex];

            using var findCmd = connection.CreateCommand();
            findCmd.CommandText = @"
                SELECT d.symbol_id
                FROM declarations d
                WHERE d.document_version_id = @docVersionId
                  AND d.full_start IS NOT NULL
                  AND d.full_end IS NOT NULL
                  AND d.full_start <= @byteOffset
                  AND d.full_end > @byteOffset
            ";

            if (!includeGenerated)
            {
                findCmd.CommandText += " AND (d.is_generated = 0 OR d.is_generated IS NULL)";
            }

            findCmd.CommandText += " ORDER BY (d.full_end - d.full_start) ASC LIMIT 1;";

            findCmd.Parameters.AddWithValue("@docVersionId", docVersionId);
            findCmd.Parameters.AddWithValue("@byteOffset", byteOffset);

            return findCmd.ExecuteScalar() as string;
        }

        public void PruneOldSnapshots(int keep = 3)
        {
            EnsureOpen();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var listCmd = connection.CreateCommand();
            listCmd.CommandText = "SELECT DISTINCT workspace_id FROM snapshots;";
            var workspaceIds = new List<string>();
            using (var reader = listCmd.ExecuteReader())
            {
                while (reader.Read())
                    workspaceIds.Add(reader.GetString(0));
            }

            foreach (var workspaceId in workspaceIds)
            {

                using var snapCmd = connection.CreateCommand();
                snapCmd.CommandText = @"
                    SELECT snapshot_id FROM snapshots
                    WHERE workspace_id = @workspaceId
                    ORDER BY built_at_utc DESC;
                ";
                snapCmd.Parameters.AddWithValue("@workspaceId", workspaceId);

                var snapshotIds = new List<string>();
                using (var snapReader = snapCmd.ExecuteReader())
                {
                    while (snapReader.Read())
                        snapshotIds.Add(snapReader.GetString(0));
                }

                if (snapshotIds.Count <= keep)
                    continue;

                var pruneIds = snapshotIds.Skip(keep).ToList();
                if (pruneIds.Count == 0)
                    continue;

                using var transaction = connection.BeginTransaction();
                try
                {
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = transaction;

                    foreach (var sid in pruneIds)
                    {

                        cmd.CommandText = "DELETE FROM edges WHERE snapshot_id = @sid;";
                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("@sid", sid);
                        cmd.ExecuteNonQuery();

                        cmd.CommandText = "DELETE FROM diagnostics WHERE snapshot_id = @sid;";
                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("@sid", sid);
                        cmd.ExecuteNonQuery();

                        cmd.CommandText = "DELETE FROM annotations WHERE snapshot_id = @sid;";
                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("@sid", sid);
                        cmd.ExecuteNonQuery();

                        cmd.CommandText = "DELETE FROM snapshot_symbols WHERE snapshot_id = @sid;";
                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("@sid", sid);
                        cmd.ExecuteNonQuery();

                        cmd.CommandText = "DELETE FROM projects WHERE snapshot_id = @sid;";
                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("@sid", sid);
                        cmd.ExecuteNonQuery();

                        cmd.CommandText = "DELETE FROM snapshot_documents WHERE snapshot_id = @sid;";
                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("@sid", sid);
                        cmd.ExecuteNonQuery();

                        cmd.CommandText = "DELETE FROM source_fts WHERE snapshot_id = @sid;";
                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("@sid", sid);
                        cmd.ExecuteNonQuery();

                        cmd.CommandText = "DELETE FROM symbol_fts WHERE snapshot_id = @sid;";
                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("@sid", sid);
                        cmd.ExecuteNonQuery();

                        cmd.CommandText = "DELETE FROM semantic_changes WHERE from_snapshot_id = @sid OR to_snapshot_id = @sid;";
                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("@sid", sid);
                        cmd.ExecuteNonQuery();

                        cmd.CommandText = "DELETE FROM snapshots WHERE snapshot_id = @sid;";
                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("@sid", sid);
                        cmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        private static string? DeriveParentTypeDocCommentId(string docCommentId)
        {
            if (string.IsNullOrEmpty(docCommentId))
                return null;

            var kind = docCommentId[0];

            if (kind == 'T' || kind == 'N')
                return null;

            if (docCommentId.Length < 3 || docCommentId[1] != ':')
                return null;

            var afterPrefix = docCommentId[2..];

            var lastDot = afterPrefix.LastIndexOf('.');
            if (lastDot < 0)
                return null;

            var parentTypeName = afterPrefix[..lastDot];
            return "T:" + parentTypeName;
        }
    }
}

