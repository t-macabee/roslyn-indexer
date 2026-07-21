using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_013_ClearDefaultProjects : IMigration
    {
        public int Version => 13;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            command.CommandText = @"
                DELETE FROM project_references
                WHERE project_id IN (
                    SELECT project_id FROM projects WHERE name = 'DefaultProject'
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = @"
                DELETE FROM projects WHERE name = 'DefaultProject';
            ";
            command.ExecuteNonQuery();
        }
    }
}
