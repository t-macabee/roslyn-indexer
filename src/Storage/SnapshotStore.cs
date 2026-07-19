using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

public sealed class SnapshotStore : ISnapshotStore
{
    private readonly string _dbPath;

    public SnapshotStore(string dbPath)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
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

    public void RunMigrations()
    {
        new MigrationRunner(_dbPath).RunMigrations();
    }

    public int GetCurrentSchemaVersion()
    {
        return new MigrationRunner(_dbPath).GetCurrentSchemaVersion();
    }

    public void ValidateSchema(int expectedVersion)
    {
        var actual = GetCurrentSchemaVersion();
        if (actual != expectedVersion)
            throw new InvalidOperationException($"Schema version mismatch: expected {expectedVersion}, got {actual}.");
    }

    public void SaveWorkspace(string id, string gitRoot, string solutionPath, DateTime createdAtUtc)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO workspaces (workspace_id, git_root, solution_path)
            VALUES (@workspaceId, @gitRoot, @solutionPath);
        ";
        command.Parameters.AddWithValue("@workspaceId", id);
        command.Parameters.AddWithValue("@gitRoot", gitRoot);
        command.Parameters.AddWithValue("@solutionPath", solutionPath);
        command.ExecuteNonQuery();
    }

    public void SaveSnapshot(SnapshotManifest manifest)
    {
        using var connection = CreateConnection();
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
                INSERT INTO snapshots (snapshot_id, workspace_id, built_at_utc, sdk_version, compiler_version,database_schema_version, output_schema_version, extractor_version,tool_version, previous_snapshot_id) VALUES (@snapshotId, @workspaceId, @builtAtUtc, @sdkVersion, @compilerVersion,@databaseSchemaVersion, @outputSchemaVersion, @extractorVersion,@toolVersion, @previousSnapshotId);
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
                    INSERT OR IGNORE INTO document_versions (document_version_id, document_id, content_hash, content, encoding, byte_count, line_starts) VALUES (@documentVersionId, @documentId, @contentHash, @content, @encoding, @byteCount, @lineStarts);
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
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE snapshots SET status = 'in_progress' WHERE snapshot_id = @snapshotId;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.ExecuteNonQuery();
    }

    public void MarkSnapshotComplete(string snapshotId)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE snapshots SET status = 'complete' WHERE snapshot_id = @snapshotId;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.ExecuteNonQuery();
    }

    public SnapshotManifest? LoadLatestSnapshot(string workspaceId)
    {
        using var connection = CreateConnection();
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
        command.Parameters.AddWithValue("@workspaceId", workspaceId);

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
            documents.Add(new DocumentVersion
            {
                DocumentId = docReader.GetString(0),
                FilePath = docReader.GetString(1),
                ContentHash = docReader.GetString(2),
                Encoding = docReader.IsDBNull(3) ? "" : docReader.GetString(3),
                LineStart = lineStarts,
                CreatedAtUtc = DateTime.MinValue,
            });
        }

        return new SnapshotManifest
        {
            SnapshotId = snapshotId,
            WorkspaceId = workspaceIdStr,
            GitRoot = gitRoot,
            SolutionPath = solutionPath,
            SdkVersion = sdkVersion ?? "",
            CompilerVersion = compilerVersion ?? "",
            CreatedAtUtc = builtAtUtc,
            Documents = documents,
        };
    }

    public string? GetLatestSnapshotId(string? workspaceId = null)
    {
        using var connection = CreateConnection();
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
        using var connection = CreateConnection();
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

    public List<string> GetSnapshotIds(string workspaceId)
    {
        using var connection = CreateConnection();
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

    public void SaveSnapshotDocuments(string snapshotId, IEnumerable<(string DocumentId, string DocumentVersionId)> entries)
    {
        using var connection = CreateConnection();
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

    public Dictionary<string, string> GetDocumentVersionIdsByPath(string snapshotId)
    {
        using var connection = CreateConnection();
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
        using var connection = CreateConnection();
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

    public void SaveSnapshotSymbols(string snapshotId, IEnumerable<string> symbolIds)
    {
        using var connection = CreateConnection();
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

    public void CopySnapshotSymbols(string fromSnapshotId, string toSnapshotId)
    {
        using var connection = CreateConnection();
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

    public void DeleteSnapshotSymbolsBySymbolIds(string snapshotId, IEnumerable<string> symbolIds)
    {
        using var connection = CreateConnection();
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

    public List<string> GetSymbolIdsInSnapshot(string snapshotId)
    {
        using var connection = CreateConnection();
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

    public void PruneOldSnapshots(int keep = 3)
    {
        using var connection = CreateConnection();

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
}
