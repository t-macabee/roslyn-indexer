using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class SnapshotSymbolStore(string dbPath)
{
    private readonly string _dbPath = dbPath;

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    internal void SaveSnapshotSymbols(string snapshotId, IEnumerable<string> symbolIds)
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

    internal void CopySnapshotSymbols(string fromSnapshotId, string toSnapshotId)
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

    internal void DeleteSnapshotSymbolsBySymbolIds(string snapshotId, IEnumerable<string> symbolIds)
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

    internal List<string> GetSymbolIdsInSnapshot(string snapshotId)
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
}
