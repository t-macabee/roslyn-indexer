using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_003_SymbolTables : IMigration
    {
        public int Version => 3;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS symbols (
                    symbol_id        TEXT PRIMARY KEY,
                    doc_comment_id   TEXT NOT NULL,
                    assembly_identity TEXT NOT NULL,
                    kind             TEXT NOT NULL,
                    metadata_json    TEXT,
                    fqn              TEXT
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_symbols_fqn ON symbols(fqn);";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_symbols_doc_comment ON symbols(doc_comment_id);";
            command.ExecuteNonQuery();

            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS declarations (
                    declaration_id      INTEGER PRIMARY KEY AUTOINCREMENT,
                    symbol_id           TEXT NOT NULL REFERENCES symbols(symbol_id),
                    document_version_id TEXT NOT NULL REFERENCES document_versions(document_version_id),
                    full_start          INTEGER,
                    full_end            INTEGER,
                    signature_start     INTEGER,
                    signature_end       INTEGER,
                    body_start          INTEGER,
                    body_end            INTEGER,
                    name_start          INTEGER,
                    name_end            INTEGER,
                    is_partial          INTEGER NOT NULL DEFAULT 0
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_declarations_symbol_id ON declarations(symbol_id);";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_declarations_doc_version ON declarations(document_version_id);";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_declarations_unique ON declarations(symbol_id, document_version_id);";
            command.ExecuteNonQuery();

            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS partial_declarations (
                    symbol_id      TEXT    NOT NULL REFERENCES symbols(symbol_id),
                    declaration_id INTEGER NOT NULL REFERENCES declarations(declaration_id),
                    PRIMARY KEY (symbol_id, declaration_id)
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS snapshot_symbols (
                    snapshot_id TEXT NOT NULL REFERENCES snapshots(snapshot_id),
                    symbol_id   TEXT NOT NULL REFERENCES symbols(symbol_id),
                    PRIMARY KEY (snapshot_id, symbol_id)
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_snapshot_symbols_snapshot_id ON snapshot_symbols(snapshot_id);";
            command.ExecuteNonQuery();
        }
    }
}

