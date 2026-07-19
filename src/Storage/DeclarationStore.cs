using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

public sealed class DeclarationStore : IDeclarationStore
{
    private readonly string _dbPath;

    public DeclarationStore(string dbPath)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    public void SaveDeclarations(string snapshotId, IEnumerable<SymbolDeclaration> declarations)
    {
        using var connection = CreateConnection();
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
                    INSERT INTO declarations (symbol_id, document_version_id,full_start, full_end,signature_start, signature_end,body_start, body_end,name_start, name_end,is_partial,is_generated,generator_identity) VALUES (@symbolId, @documentVersionId,@fullStart, @fullEnd,@signatureStart, @signatureEnd,@bodyStart, @bodyEnd,@nameStart, @nameEnd,@isPartial,@isGenerated,@generatorIdentity)
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
        using var connection = CreateConnection();
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

        return ReadSymbolInfo(reader);
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

    public void DeleteDeclarationsByDocumentVersionIds(IEnumerable<string> documentVersionIds)
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

    public List<string> GetSymbolIdsByDocumentVersionIds(string snapshotId, IEnumerable<string> documentVersionIds)
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

    public string? ResolveSymbolByLocation(string relativePath, int line, string snapshotId, bool includeGenerated = false)
    {
        using var connection = CreateConnection();

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

    internal static string? SliceToString(byte[] content, int start, int end)
    {
        if (start < 0 || end > content.Length || start > end)
            return null;
        int length = end - start;
        if (length == 0)
            return string.Empty;
        return Encoding.UTF8.GetString(content, start, length);
    }

    internal static SymbolInfo? ReadSymbolInfo(SqliteDataReader reader)
    {
        var sid = new SymbolId(docCommentId: reader.GetString(1),
            assemblyIdentity: reader.GetString(2),
            fullyQualifiedName: reader.IsDBNull(4) ? null : reader.GetString(4));

        var kindStr = reader.GetString(3);
        Enum.TryParse<SymbolKind>(kindStr, ignoreCase: true, out var kind);

        return new SymbolInfo(symbolId: sid,kind: kind,fullyQualifiedName: reader.IsDBNull(4) ? null : reader.GetString(4),
            metadataJson: reader.IsDBNull(5) ? null : reader.GetString(5),
            declarationCount: reader.GetInt32(6),
            isPartial: reader.GetInt32(7) == 1);
    }

    internal static string? DeriveParentTypeDocCommentId(string docCommentId)
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
