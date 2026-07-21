using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class SnapshotLifecycleStore(string dbPath)
{
    private readonly string _dbPath = dbPath;

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    internal void SaveWorkspace(string id, string gitRoot, string solutionPath, DateTime createdAtUtc)
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

    internal void SaveSnapshot(SnapshotRow manifest)
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
                INSERT INTO snapshots (snapshot_id, workspace_id, built_at_utc, sdk_version, compiler_version,database_schema_version, output_schema_version, extractor_version,tool_version, previous_snapshot_id, status) VALUES (@snapshotId, @workspaceId, @builtAtUtc, @sdkVersion, @compilerVersion,@databaseSchemaVersion, @outputSchemaVersion, @extractorVersion,@toolVersion, @previousSnapshotId, 'in_progress');
            ";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@snapshotId", manifest.SnapshotId);
            command.Parameters.AddWithValue("@workspaceId", manifest.WorkspaceId);
            command.Parameters.AddWithValue("@builtAtUtc", manifest.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@sdkVersion", manifest.SdkVersion ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@compilerVersion", manifest.CompilerVersion ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@databaseSchemaVersion", (object)manifest.DatabaseSchemaVersion);
            command.Parameters.AddWithValue("@outputSchemaVersion", (object)manifest.OutputSchemaVersion);
            command.Parameters.AddWithValue("@extractorVersion", (object?)manifest.ExtractorVersion ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@toolVersion", (object?)manifest.ToolVersion ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@previousSnapshotId", (object?)manifest.PreviousSnapshotId ?? (object)DBNull.Value);
            command.ExecuteNonQuery();

            if (manifest.Projects.Any())
            {
                foreach (var project in manifest.Projects)
                {
                    command.CommandText = @"
                        INSERT INTO projects (snapshot_id, name, target_framework)
                        VALUES (@snapshotId, @name, @targetFramework);
                        SELECT last_insert_rowid();
                    ";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@snapshotId", manifest.SnapshotId);
                    command.Parameters.AddWithValue("@name", project.Name);
                    command.Parameters.AddWithValue("@targetFramework", (object?)project.TargetFramework ?? (object)DBNull.Value);
                    var projectId = command.ExecuteScalar();

                    if (project.References.Count > 0 && projectId != null)
                    {
                        foreach (var reference in project.References)
                        {
                            command.CommandText = @"
                                INSERT INTO project_references (project_id, referenced_project_name)
                                VALUES (@projectId, @referencedProjectName);
                            ";
                            command.Parameters.Clear();
                            command.Parameters.AddWithValue("@projectId", (long)projectId);
                            command.Parameters.AddWithValue("@referencedProjectName", reference);
                            command.ExecuteNonQuery();
                        }
                    }
                }
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

    internal void MarkSnapshotInProgress(string snapshotId)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE snapshots SET status = 'in_progress' WHERE snapshot_id = @snapshotId;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.ExecuteNonQuery();
    }

    internal void MarkSnapshotComplete(string snapshotId)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE snapshots SET status = 'complete' WHERE snapshot_id = @snapshotId;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.ExecuteNonQuery();
    }

    internal SnapshotRow? LoadLatestSnapshot(string workspaceId)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT s.snapshot_id, s.workspace_id, w.git_root, w.solution_path,
                   s.sdk_version, s.compiler_version, s.built_at_utc,
                   s.database_schema_version, s.output_schema_version,
                   s.extractor_version, s.tool_version, s.previous_snapshot_id
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
        var builtAtUtc = DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        var databaseSchemaVersion = reader.IsDBNull(7) ? 0 : reader.GetInt32(7);
        var outputSchemaVersion = reader.IsDBNull(8) ? 0 : reader.GetInt32(8);
        var extractorVersion = reader.IsDBNull(9) ? null : reader.GetString(9);
        var toolVersion = reader.IsDBNull(10) ? null : reader.GetString(10);
        var previousSnapshotId = reader.IsDBNull(11) ? null : reader.GetString(11);

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

        return new SnapshotRow
        {
            SnapshotId = snapshotId,
            WorkspaceId = workspaceIdStr,
            GitRoot = gitRoot,
            SolutionPath = solutionPath,
            SdkVersion = sdkVersion ?? "",
            CompilerVersion = compilerVersion ?? "",
            CreatedAtUtc = builtAtUtc,
            Documents = documents,
            DatabaseSchemaVersion = databaseSchemaVersion,
            OutputSchemaVersion = outputSchemaVersion,
            ExtractorVersion = extractorVersion ?? "",
            ToolVersion = toolVersion ?? "",
            PreviousSnapshotId = previousSnapshotId,
        };
    }

    internal string? GetLatestSnapshotId(string? workspaceId = null)
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

    internal List<string> GetSnapshotIds(string workspaceId)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT snapshot_id
            FROM snapshots
            WHERE workspace_id = @workspaceId
              AND status = 'complete'
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
}
