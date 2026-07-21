using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_014_AddCrossGeneratedFlag : IMigration
    {
        public int Version => 14;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            command.CommandText = "ALTER TABLE edges ADD COLUMN is_cross_generated INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();

            // Migrate existing composite provenance values: extract :cross_generated suffix,
            // clean the provenance, and set the flag.
            command.CommandText = @"
                UPDATE edges
                SET is_cross_generated = 1,
                    provenance = REPLACE(provenance, ':cross_generated', '')
                WHERE provenance LIKE '%:cross_generated';
            ";
            command.ExecuteNonQuery();
        }
    }
}
