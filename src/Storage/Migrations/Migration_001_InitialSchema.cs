using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_001_InitialSchema : IMigration
    {
        public int Version => 1;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS schema_metadata (
                    version INTEGER NOT NULL,
                    applied_at_utc TEXT NOT NULL,
                    migration_id TEXT NOT NULL
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS workspaces (
                    workspace_id TEXT PRIMARY KEY,
                    git_root TEXT NOT NULL,
                    solution_path TEXT NOT NULL
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS snapshots (
                    snapshot_id TEXT PRIMARY KEY,
                    workspace_id TEXT NOT NULL REFERENCES workspaces(workspace_id),
                    built_at_utc TEXT NOT NULL,
                    sdk_version TEXT,
                    compiler_version TEXT,
                    database_schema_version INTEGER,
                    output_schema_version INTEGER,
                    extractor_version TEXT,
                    tool_version TEXT,
                    previous_snapshot_id TEXT
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS projects (
                    project_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    snapshot_id TEXT NOT NULL REFERENCES snapshots(snapshot_id),
                    name TEXT NOT NULL,
                    target_framework TEXT
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS project_references (
                    project_id INTEGER NOT NULL REFERENCES projects(project_id),
                    referenced_project_name TEXT NOT NULL
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS documents (
                    document_id TEXT PRIMARY KEY,
                    relative_path TEXT NOT NULL
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS document_versions (
                    document_version_id TEXT PRIMARY KEY,
                    document_id TEXT NOT NULL REFERENCES documents(document_id),
                    content_hash TEXT NOT NULL,
                    content BLOB,
                    encoding TEXT,
                    byte_count INTEGER
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS snapshot_documents (
                    snapshot_id TEXT NOT NULL REFERENCES snapshots(snapshot_id),
                    document_version_id TEXT NOT NULL REFERENCES document_versions(document_version_id)
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_snapshots_workspace_id ON snapshots(workspace_id);";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_snapshot_documents_snapshot_id ON snapshot_documents(snapshot_id);";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_documents_relative_path ON documents(relative_path);";
            command.ExecuteNonQuery();
        }
    }
}

