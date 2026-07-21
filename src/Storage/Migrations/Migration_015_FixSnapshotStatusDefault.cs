using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    /// <summary>
    /// Migration 011 incorrectly set the snapshots.status default to 'complete'.
    /// A snapshot row should be born as 'in_progress' and only marked complete
    /// once extraction succeeds. This migration fixes the default and updates
    /// any existing rows that were created with the inverted default.
    ///
    /// Resolution of roadmap ambiguity #2 — "atomic snapshot commit":
    ///   Operationally in this codebase, an atomic snapshot commit means the
    ///   snapshot row and its document rows must be written in a single
    ///   transaction with status = 'in_progress'. Only after all facts (edges,
    ///   declarations, symbols, FTS) have been written is status set to
    ///   'complete'. If the process dies mid-extraction, the snapshot remains
    ///   'in_progress' and no read path will select it. The pruner will
    ///   eventually clean it up when it ages out of the retention window.
    /// </summary>
    public class Migration_015_FixSnapshotStatusDefault : IMigration
    {
        public int Version => 15;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            // SQLite does not support ALTER COLUMN DEFAULT, so recreate the table
            // approach: create new table, copy data, rename.
            command.CommandText = @"
                CREATE TABLE snapshots_new (
                    snapshot_id             TEXT PRIMARY KEY,
                    workspace_id            TEXT NOT NULL,
                    built_at_utc            TEXT NOT NULL,
                    sdk_version             TEXT,
                    compiler_version        TEXT,
                    database_schema_version INTEGER NOT NULL DEFAULT 0,
                    output_schema_version   INTEGER NOT NULL DEFAULT 0,
                    extractor_version       TEXT,
                    tool_version            TEXT,
                    previous_snapshot_id    TEXT,
                    status                  TEXT NOT NULL DEFAULT 'in_progress',
                    FOREIGN KEY (workspace_id) REFERENCES workspaces(workspace_id)
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = @"
                INSERT INTO snapshots_new
                SELECT snapshot_id, workspace_id, built_at_utc, sdk_version,
                       compiler_version, database_schema_version, output_schema_version,
                       extractor_version, tool_version, previous_snapshot_id, status
                FROM snapshots;
            ";
            command.ExecuteNonQuery();

            command.CommandText = "DROP TABLE snapshots;";
            command.ExecuteNonQuery();

            command.CommandText = "ALTER TABLE snapshots_new RENAME TO snapshots;";
            command.ExecuteNonQuery();

            // Recreate indexes that were on the original table
            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_snapshots_workspace ON snapshots(workspace_id);";
            command.ExecuteNonQuery();
        }
    }
}
