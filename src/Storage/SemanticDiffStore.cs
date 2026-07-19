using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

public sealed class SemanticDiffStore : ISemanticDiffStore
{
    private readonly string _dbPath;

    public SemanticDiffStore(string dbPath)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    public void SaveSemanticChanges(string fromSnapshotId, string toSnapshotId, IEnumerable<SemanticChange> changes)
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            foreach (var change in changes)
            {
                command.CommandText = @"
                    INSERT OR REPLACE INTO semantic_changes (change_id, from_snapshot_id, to_snapshot_id,change_type, symbol_id, detail_json, created_at_utc) VALUES (@changeId, @fromSnapshotId, @toSnapshotId,@changeType, @symbolId, @detailJson, @createdAtUtc);
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
        using var connection = CreateConnection();
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
            results.Add(new SemanticChange
            {
                ChangeId = reader.GetString(0),
                FromSnapshotId = reader.GetString(1),
                ToSnapshotId = reader.GetString(2),
                ChangeType = reader.GetString(3),
                SymbolId = reader.GetString(4),
                DetailJson = reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedAtUtc = DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind)
            });
        }
        return results;
    }
}
