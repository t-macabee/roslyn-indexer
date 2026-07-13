using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_002_AddLineStarts : IMigration
    {
        public int Version => 2;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE document_versions ADD COLUMN line_starts TEXT;";
            command.ExecuteNonQuery();
        }
    }
}

