using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class DeclarationMaintenanceStore(string dbPath)
{
    private readonly string _dbPath = dbPath;

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    internal void DeleteDeclarationsByDocumentVersionIds(IEnumerable<string> documentVersionIds)
    {
        using var connection = CreateConnection();
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

    internal List<string> GetSymbolIdsByDocumentVersionIds(string snapshotId, IEnumerable<string> documentVersionIds)
    {
        using var connection = CreateConnection();
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

    internal string? ResolveSymbolByLocation(string relativePath, int line, string snapshotId, bool includeGenerated = false)
    {
        using var connection = CreateConnection();

        var (docVersionId, lineStarts) = GetDocumentLineStarts(connection, relativePath, snapshotId);
        if (docVersionId == null || lineStarts == null || lineStarts.Length == 0)
            return null;

        int lineIndex = Math.Max(0, line - 1);
        if (lineIndex >= lineStarts.Length)
            return null;

        int byteOffset = lineStarts[lineIndex];

        return FindSymbolAtOffset(connection, docVersionId, byteOffset, includeGenerated);
    }

    private static (string? DocVersionId, int[]? LineStarts) GetDocumentLineStarts(SqliteConnection connection, string relativePath, string snapshotId)
    {
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
                return (null, null);
            docVersionId = reader.GetString(0);
            lineStartsJson = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        if (lineStartsJson == null)
            return (docVersionId, null);

        return (docVersionId, JsonSerializer.Deserialize<int[]>(lineStartsJson));
    }

    private static string? FindSymbolAtOffset(SqliteConnection connection, string docVersionId, int byteOffset, bool includeGenerated)
    {
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
}
