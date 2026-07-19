using Microsoft.Data.Sqlite;
using Lurp.Storage.Migrations;

namespace Lurp.Storage
{
    public class MigrationRunner
    {
        private readonly string _dbPath;

        public MigrationRunner(string dbPath)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        }

        public void RunMigrations()
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            var currentVersion = GetCurrentSchemaVersion(connection);
            var migrations = GetMigrations().OrderBy(m => m.Version).ToList();

            foreach (var migration in migrations)
            {
                if (migration.Version <= currentVersion)
                    continue;

                using var transaction = connection.BeginTransaction();
                try
                {
                    migration.Up(connection);
                    UpdateSchemaVersion(connection, migration.Version, migration.GetType().Name, transaction);
                    transaction.Commit();
                    currentVersion = migration.Version;
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        public int GetCurrentSchemaVersion()
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            return GetCurrentSchemaVersion(connection);
        }

        private static int GetCurrentSchemaVersion(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='schema_metadata';";
            var tableExists = command.ExecuteScalar();
            if (tableExists == null || tableExists == DBNull.Value)
                return 0;

            command.CommandText = "SELECT version FROM schema_metadata ORDER BY version DESC LIMIT 1;";
            var result = command.ExecuteScalar();
            return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }

        private static void UpdateSchemaVersion(SqliteConnection connection,int version,string migrationId,SqliteTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO schema_metadata (version, applied_at_utc, migration_id)
                VALUES (@version, @appliedAtUtc, @migrationId);
            ";
            command.Parameters.AddWithValue("@version", version);
            command.Parameters.AddWithValue("@appliedAtUtc", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("@migrationId", migrationId);
            command.ExecuteNonQuery();
        }

        private static List<IMigration> GetMigrations() =>
            [
                new Migration_001_InitialSchema(),
                new Migration_002_AddLineStarts(),
                new Migration_003_SymbolTables(),
                new Migration_004_FtsSearch(),
                new Migration_005_A5OperationalTables(),
                new Migration_006_ExpandEdges(),
                new Migration_007_SemanticChanges(),
                new Migration_008_GeneratedCodeAwareness(),
                new Migration_009_PerSnapshotSymbolData(),
                new Migration_010_AddLastChangedSnapshotId(),
                new Migration_011_SnapshotStatus(),
            ];

        internal static void RunTest(string testDbPath)
        {
            Console.WriteLine("Testing migration runner...");

            if (File.Exists(testDbPath))
            {
                File.Delete(testDbPath);
            }

            var store = new SqliteIndexStore(testDbPath);
            store.Open(testDbPath);

            var initialVersion = store.GetCurrentSchemaVersion();
            Console.WriteLine($"Initial schema version: {initialVersion}");

            store.RunMigrations();

            var afterVersion = store.GetCurrentSchemaVersion();
            Console.WriteLine($"Schema version after migrations: {afterVersion}");

            store.RunMigrations();

            var secondVersion = store.GetCurrentSchemaVersion();
            Console.WriteLine($"Schema version after second run: {secondVersion}");

            var expected = GetMigrations().Max(m => m.Version);
            if (afterVersion == expected && secondVersion == expected)
            {
                Console.WriteLine($"\u2713 Migration test passed: schema version is {expected} and idempotent");
            }
            else
            {
                Console.WriteLine($"\u2717 Migration test failed: expected {expected}, got {afterVersion}/{secondVersion}");
                Environment.Exit(1);
            }

            store.Close();
        }
    }
}

