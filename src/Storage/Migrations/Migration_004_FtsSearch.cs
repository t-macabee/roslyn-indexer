using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_004_FtsSearch : IMigration
    {
        public int Version => 4;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            // Source content FTS table — indexes document text for full-text search
            // UNINDEXED columns are stored but not full-text indexed; they are used for filtering.
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

            // Symbol FTS table — indexes symbol names and FQNs for full-text search
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

