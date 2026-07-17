using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_010_AddLastChangedSnapshotId : IMigration
    {
        public int Version => 10;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            command.CommandText = @"
                ALTER TABLE documents ADD COLUMN last_changed_snapshot_id TEXT;
            ";
            command.ExecuteNonQuery();
        }
    }
}
