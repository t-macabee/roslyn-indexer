using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class DeclarationReadStore(string dbPath)
{
    private readonly string _dbPath = dbPath;

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    internal IndexedSymbolInfo? GetSymbolInfo(string symbolId, string snapshotId)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT s.symbol_id, s.doc_comment_id, s.assembly_identity, s.kind, ss.fqn, ss.metadata_json,
                   (SELECT COUNT(*) FROM declarations d
                    JOIN snapshot_documents sd ON sd.document_version_id = d.document_version_id
                    WHERE d.symbol_id = s.symbol_id AND sd.snapshot_id = @snapshotId) AS decl_count,
                   (SELECT MAX(d.is_partial) FROM declarations d
                    JOIN snapshot_documents sd ON sd.document_version_id = d.document_version_id
                    WHERE d.symbol_id = s.symbol_id AND sd.snapshot_id = @snapshotId) AS is_partial
            FROM symbols s
            JOIN snapshot_symbols ss ON ss.symbol_id = s.symbol_id
            WHERE s.symbol_id = @symbolId AND ss.snapshot_id = @snapshotId;
        ";
        command.Parameters.AddWithValue("@symbolId", symbolId);
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return DeclarationStore.ReadSymbolInfo(reader);
    }

    internal string? GetSymbolSource(string symbolId, string snapshotId, ViewKind viewKind, bool includeGenerated = false)
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

    internal string? GetContainingTypeSource(string symbolId, string snapshotId)
    {
        using var connection = CreateConnection();
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

    internal string? GetSurroundingLines(string symbolId, string snapshotId, int contextLines)
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

    private (byte[]? Content, int? Start, int? End) GetSymbolSpanContent(string symbolId, string snapshotId, string startCol, string endCol, bool includeGenerated = false)
    {
        using var connection = CreateConnection();
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
        using var connection = CreateConnection();
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
