using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_004_FtsSearch : IMigration
    {
        public int Version => 4;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            
            
            command.CommandText = @"
                CREATE VIRTUAL TABLE IF NOT EXISTS source_fts USING fts5(
                    document_path,
                    content,
                    snapshot_id UNINDEXED,
                    document_version_id UNINDEXED,
                    tokenize='unicode61'
                );
            ";
            command.ExecuteNonQuery();

            
            command.CommandText = @"
                CREATE VIRTUAL TABLE IF NOT EXISTS symbol_fts USING fts5(
                    symbol_id,
                    fqn,
                    doc_comment_id,
                    kind,
                    snapshot_id UNINDEXED,
                    tokenize='unicode61'
                );
            ";
            command.ExecuteNonQuery();
        }
    }
}

