using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

public sealed class EdgeStore : IEdgeStore
{
    private readonly string _dbPath;

    public EdgeStore(string dbPath)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    public void SaveEdges(string snapshotId, IEnumerable<EdgeRecord> edges)
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            foreach (var edge in edges)
            {
                command.CommandText = @"
                    INSERT INTO edges (snapshot_id, source_symbol_id, target_symbol_id, kind, provenance,extractor_version, source_document_path,source_start_line, source_start_column,source_end_line, source_end_column) VALUES (@snapshotId, @sourceSymbolId, @targetSymbolId, @kind, @provenance,@extractorVersion, @sourceDocumentPath,@sourceStartLine, @sourceStartColumn,@sourceEndLine, @sourceEndColumn);
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

    public void SaveDiagnostics(string snapshotId, IEnumerable<DiagnosticRecord> diagnostics)
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            foreach (var diag in diagnostics)
            {
                command.CommandText = @"
                    INSERT INTO diagnostics (snapshot_id, project_name, document_path, severity, id, message,start_line, start_column, end_line, end_column)
                    VALUES (@snapshotId, @projectName, @documentPath, @severity, @id, @message,@startLine, @startColumn, @endLine, @endColumn);
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

    public void SaveAnnotations(string snapshotId, IEnumerable<AnnotationRecord> annotations)
    {
        using var connection = CreateConnection();
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

    public List<EdgeRecord> GetEdges(string snapshotId, string? symbolId = null)
    {
        using var connection = CreateConnection();
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
        using var connection = CreateConnection();
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
        using var connection = CreateConnection();
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
        using var connection = CreateConnection();
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

    public void DeleteEdgesByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths)
    {
        using var connection = CreateConnection();
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

    public void DeleteEdgesWithNullDocumentPathForAssemblies(string snapshotId, IEnumerable<string> assemblyIdentities)
    {
        var identityList = assemblyIdentities as IReadOnlyCollection<string> ?? assemblyIdentities.ToList();
        if (identityList.Count == 0)
            return;

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM edges
            WHERE snapshot_id = @snapshotId
              AND source_document_path IS NULL
              AND (" + string.Join(" OR ", identityList.Select((_, i) => $"source_symbol_id LIKE @p{i} ESCAPE '\\'")) + @");
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        int i = 0;
        foreach (var identity in identityList)
        {
            var escaped = identity.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            command.Parameters.AddWithValue($"@p{i++}", "%|" + escaped);
        }
        command.ExecuteNonQuery();
    }

    public void CopyEdgesToSnapshot(string fromSnapshotId, string toSnapshotId)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO edges (snapshot_id, source_symbol_id, target_symbol_id, kind, provenance,extractor_version, source_document_path,source_start_line, source_start_column,source_end_line, source_end_column)
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

    public void CopySnapshotDiagnostics(string fromSnapshotId, string toSnapshotId)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO diagnostics (snapshot_id, project_name, document_path, severity, id, message,start_line, start_column, end_line, end_column)
            SELECT @toSnapshotId, project_name, document_path, severity, id, message,
                   start_line, start_column, end_line, end_column
            FROM diagnostics
            WHERE snapshot_id = @fromSnapshotId;
        ";
        command.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
        command.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);
        command.ExecuteNonQuery();
    }

    public void DeleteDiagnosticsByProjectNames(string snapshotId, IEnumerable<string> projectNames)
    {
        var nameList = projectNames as IReadOnlyCollection<string> ?? projectNames.ToList();
        if (nameList.Count == 0)
            return;

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM diagnostics
            WHERE snapshot_id = @snapshotId
              AND project_name IN (" + string.Join(", ", nameList.Select((_, i) => $"@p{i}")) + @");
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        int i = 0;
        foreach (var name in nameList)
            command.Parameters.AddWithValue($"@p{i++}", name);
        command.ExecuteNonQuery();
    }

    public List<DiagnosticRecord> GetDiagnostics(string snapshotId, string? projectName = null)
    {
        using var connection = CreateConnection();
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
            results.Add(new DiagnosticRecord(projectName: reader.GetString(0),
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

    public List<AnnotationRecord> GetAnnotations(string snapshotId, string? symbolId = null)
    {
        using var connection = CreateConnection();
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
            results.Add(new AnnotationRecord(symbolId: reader.GetString(0),
                kind: reader.GetString(1),
                value: reader.GetString(2)));
        }
        return results;
    }

    private static List<EdgeRecord> ReadEdgeRecords(SqliteCommand command)
    {
        var results = new List<EdgeRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new EdgeRecord(sourceSymbolId: reader.GetString(0),
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
}
