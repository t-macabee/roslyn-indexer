using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_005_A5OperationalTables : IMigration
    {
        public int Version => 5;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            // edges — type-level dependency relationships with provenance
            // No FK constraint on snapshot_id — integrity managed by app layer.
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS edges (
                    edge_id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    snapshot_id    TEXT    NOT NULL,
                    source_symbol_id TEXT  NOT NULL,
                    target_symbol_id TEXT  NOT NULL,
                    kind           TEXT    NOT NULL,
                    provenance     TEXT
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_edges_snapshot_id ON edges(snapshot_id);";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_edges_source ON edges(source_symbol_id);";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_edges_target ON edges(target_symbol_id);";
            command.ExecuteNonQuery();

            // diagnostics — compiler warnings/errors per project, snapshot-bound
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS diagnostics (
                    diagnostic_id  INTEGER PRIMARY KEY AUTOINCREMENT,
                    snapshot_id    TEXT    NOT NULL,
                    project_name   TEXT    NOT NULL,
                    document_path  TEXT,
                    severity       TEXT    NOT NULL,
                    id             TEXT    NOT NULL,
                    message        TEXT    NOT NULL,
                    start_line     INTEGER,
                    start_column   INTEGER,
                    end_line       INTEGER,
                    end_column     INTEGER
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_diagnostics_snapshot_id ON diagnostics(snapshot_id);";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_diagnostics_project ON diagnostics(project_name);";
            command.ExecuteNonQuery();

            // annotations — per-symbol annotations (supplemental metadata)
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS annotations (
                    annotation_id  INTEGER PRIMARY KEY AUTOINCREMENT,
                    snapshot_id    TEXT    NOT NULL,
                    symbol_id      TEXT    NOT NULL,
                    kind           TEXT    NOT NULL,
                    value          TEXT    NOT NULL
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_annotations_snapshot_id ON annotations(snapshot_id);";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_annotations_symbol_id ON annotations(symbol_id);";
            command.ExecuteNonQuery();
        }
    }
}

