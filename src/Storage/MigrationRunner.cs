using System;
using System.Collections.Generic;
using System.Linq;
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

        private static void UpdateSchemaVersion(
            SqliteConnection connection,
            int version,
            string migrationId,
            SqliteTransaction transaction)
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
            new List<IMigration>
            {
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
            };
    }
}

