using System;
using System.Data;
using Microsoft.Data.Sqlite;
using Lurp.Storage.Migrations;

namespace Lurp.Storage.Migrations
{
    public class Migration_008_GeneratedCodeAwareness : IMigration
    {
        public int Version => 8;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            try
            {
                command.CommandText = @"ALTER TABLE declarations ADD COLUMN is_generated INTEGER NOT NULL DEFAULT 0;";
                command.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
            }

            try
            {
                command.CommandText = @"ALTER TABLE declarations ADD COLUMN generator_identity TEXT;";
                command.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
            }

            command.CommandText = @"CREATE INDEX IF NOT EXISTS idx_declarations_generated ON declarations(is_generated);";
            command.ExecuteNonQuery();
        }
    }
}