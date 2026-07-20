using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class SnapshotTimingStore(string dbPath)
{
    private readonly string _dbPath = dbPath;

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    internal void SaveTimings(string snapshotId, IEnumerable<SnapshotTimingRow> timings)
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO snapshot_timings (snapshot_id, step_name, elapsed_ms, created_at_utc)
                VALUES (@snapshotId, @stepName, @elapsedMs, @createdAtUtc);
            ";

            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            var stepNameParam = command.CreateParameter();
            stepNameParam.ParameterName = "@stepName";
            command.Parameters.Add(stepNameParam);
            var elapsedMsParam = command.CreateParameter();
            elapsedMsParam.ParameterName = "@elapsedMs";
            command.Parameters.Add(elapsedMsParam);
            command.Parameters.AddWithValue("@createdAtUtc", DateTime.UtcNow.ToString("O"));

            foreach (var timing in timings)
            {
                stepNameParam.Value = timing.StepName;
                elapsedMsParam.Value = timing.ElapsedMs;
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

    internal List<SnapshotTimingRow> GetTimings(string snapshotId)
    {
        var results = new List<SnapshotTimingRow>();
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT step_name, elapsed_ms, created_at_utc
            FROM snapshot_timings
            WHERE snapshot_id = @snapshotId
            ORDER BY timing_id;
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SnapshotTimingRow(
                stepName: reader.GetString(0),
                elapsedMs: reader.GetInt64(1),
                createdAtUtc: DateTime.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        return results;
    }

    internal List<(string SnapshotId, string StepName, long ElapsedMs)> GetLatestTimings(int? limit = null)
    {
        var results = new List<(string, string, long)>();
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT t.snapshot_id, t.step_name, t.elapsed_ms
            FROM snapshot_timings t
            JOIN snapshots s ON s.snapshot_id = t.snapshot_id
            ORDER BY s.built_at_utc DESC, t.timing_id
        ";
        if (limit.HasValue)
        {
            command.CommandText += " LIMIT @limit";
            command.Parameters.AddWithValue("@limit", limit.Value);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }
        return results;
    }
}
