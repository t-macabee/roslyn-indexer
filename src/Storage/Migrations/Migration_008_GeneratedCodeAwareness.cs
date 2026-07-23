using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_008_GeneratedCodeAwareness : IMigration
    {
        public int Version => 8;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            var existingColumns = GetColumnNames(command, "declarations");

            if (!existingColumns.Contains("is_generated"))
            {
                command.CommandText = @"ALTER TABLE declarations ADD COLUMN is_generated INTEGER NOT NULL DEFAULT 0;";
                command.ExecuteNonQuery();
            }

            if (!existingColumns.Contains("generator_identity"))
            {
                command.CommandText = @"ALTER TABLE declarations ADD COLUMN generator_identity TEXT;";
                command.ExecuteNonQuery();
            }

            command.CommandText = @"CREATE INDEX IF NOT EXISTS idx_declarations_generated ON declarations(is_generated);";
            command.ExecuteNonQuery();
        }

        private static HashSet<string> GetColumnNames(SqliteCommand command, string tableName)
        {
            command.CommandText = $"PRAGMA table_info({tableName});";
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(1));
            return columns;
        }
    }
}