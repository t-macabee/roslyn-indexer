using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_009_PerSnapshotSymbolData : IMigration
    {
        public int Version => 9;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            command.CommandText = @"
                ALTER TABLE snapshot_symbols ADD COLUMN fqn TEXT;
            ";
            command.ExecuteNonQuery();

            command.CommandText = @"
                ALTER TABLE snapshot_symbols ADD COLUMN metadata_json TEXT;
            ";
            command.ExecuteNonQuery();
        }
    }
}
