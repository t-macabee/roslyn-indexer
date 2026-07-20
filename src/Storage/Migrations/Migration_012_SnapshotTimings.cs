using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_012_SnapshotTimings : IMigration
    {
        public int Version => 12;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS snapshot_timings (
                    timing_id   INTEGER PRIMARY KEY AUTOINCREMENT,
                    snapshot_id TEXT NOT NULL,
                    step_name   TEXT NOT NULL,
                    elapsed_ms  INTEGER NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    FOREIGN KEY (snapshot_id) REFERENCES snapshots(snapshot_id)
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_snapshot_timings_snapshot_id ON snapshot_timings(snapshot_id);";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_snapshot_timings_step_name ON snapshot_timings(step_name);";
            command.ExecuteNonQuery();
        }
    }
}
