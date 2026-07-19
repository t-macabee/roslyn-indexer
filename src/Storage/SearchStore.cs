using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

public sealed class SearchStore : ISearchStore
{
    private readonly string _dbPath;

    public SearchStore(string dbPath)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    public void BuildSearchIndex(string snapshotId)
    {
        using var connection = CreateConnection();
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
        using var connection = CreateConnection();
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
            results.Add(new SourceSearchResult(documentPath: reader.GetString(0),
                snippet: reader.IsDBNull(1) ? "" : reader.GetString(1)));
        }
        return results;
    }

    public List<SymbolSearchResult> SearchSymbols(string query, string snapshotId, int limit = 20, bool includeGenerated = false, string? kind = null)
    {
        using var connection = CreateConnection();
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
            results.Add(new SymbolSearchResult(symbolId: reader.GetString(0),
                fullyQualifiedName: reader.IsDBNull(1) ? "" : reader.GetString(1),
                kind: reader.GetString(3),
                docCommentId: reader.GetString(2)));
        }
        return results;
    }

    public IndexedSymbolInfo? ResolveSymbolByFqn(string fqn, string snapshotId, bool includeGenerated = false)
    {
        using var connection = CreateConnection();
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
            return DeclarationStore.ReadSymbolInfo(reader);

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
            return DeclarationStore.ReadSymbolInfo(reader2);

        return null;
    }
}
