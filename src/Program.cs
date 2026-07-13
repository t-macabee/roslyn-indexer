using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using RoslynIndexer.Storage;

namespace RoslynIndexer
{
    public class Program
    {
        private static IIndexStore? _indexStore;

        public static void Main(string[] args)
        {
            if (args.Contains("--mode=get-source"))
            {
                RunGetSource(args);
                return;
            }

            var solutionPathArg = args.FirstOrDefault(a => a.StartsWith("--solution="))?.Split('=', 2)[1]
                ?? Environment.GetEnvironmentVariable("INDEXER_SOLUTION_PATH");
            if (string.IsNullOrEmpty(solutionPathArg) || !File.Exists(solutionPathArg))
            {
                Console.Error.WriteLine("ERROR: --solution=path or INDEXER_SOLUTION_PATH is required and must point to an existing .sln file.");
                Environment.Exit(1);
            }

            var outputDirArg = args.FirstOrDefault(a => a.StartsWith("--output-dir="))?.Split('=', 2)[1]
                ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
            if (string.IsNullOrEmpty(outputDirArg))
            {
                Console.Error.WriteLine("ERROR: --output-dir=path or INDEXER_OUTPUT_DIR is required.");
                Environment.Exit(1);
            }

            var outputDir = Path.GetFullPath(outputDirArg);
            var dbPath = Path.Combine(outputDir, "index.db");

            _indexStore = new SqliteIndexStore(dbPath);
            _indexStore.Open(dbPath);
            _indexStore.RunMigrations();
            _indexStore.ValidateSchema(expectedVersion: VersionConstants.DatabaseSchemaVersion);

            try
            {
                if (args.Contains("--mode=test-migration"))
                {
                    TestMigration();
                }
                else if (args.Contains("--mode=status"))
                {
                    ShowStatus();
                }
            }
            finally
            {
                _indexStore.Close();
            }
        }

        private static void RunGetSource(string[] args)
        {
            var documentArg = args.FirstOrDefault(a => a.StartsWith("--document="))?.Split('=', 2)[1];
            if (string.IsNullOrEmpty(documentArg))
            {
                Console.Error.WriteLine("ERROR: --document=<relative-path> is required for --mode=get-source.");
                Environment.Exit(1);
            }

            var snapshotArg = args.FirstOrDefault(a => a.StartsWith("--snapshot="))?.Split('=', 2)[1];

            var outputDirArg = args.FirstOrDefault(a => a.StartsWith("--output-dir="))?.Split('=', 2)[1]
                ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
            if (string.IsNullOrEmpty(outputDirArg))
            {
                Console.Error.WriteLine("ERROR: --output-dir=path or INDEXER_OUTPUT_DIR is required.");
                Environment.Exit(1);
            }

            var dbPath = Path.Combine(Path.GetFullPath(outputDirArg), "index.db");
            if (!File.Exists(dbPath))
            {
                Console.Error.WriteLine("ERROR: Index database not found at " + dbPath);
                Environment.Exit(1);
            }

            _indexStore = new SqliteIndexStore(dbPath);
            _indexStore.Open(dbPath);

            try
            {
                
                string? source;
                if (!string.IsNullOrEmpty(snapshotArg))
                {
                    source = _indexStore.GetSource(documentArg, snapshotArg);
                }
                else
                {
                    
                    using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                    connection.Open();
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT snapshot_id FROM snapshots ORDER BY built_at_utc DESC LIMIT 1;";
                    var latestSnapshot = cmd.ExecuteScalar() as string;
                    if (latestSnapshot == null)
                    {
                        Console.Error.WriteLine("ERROR: No snapshots found in the database.");
                        Environment.Exit(1);
                    }
                    source = _indexStore.GetSource(documentArg, latestSnapshot);
                }

                if (source == null)
                {
                    Console.Error.WriteLine($"ERROR: Document '{documentArg}' not found in snapshot.");
                    Environment.Exit(1);
                }

                Console.Write(source);
            }
            finally
            {
                _indexStore.Close();
            }
        }

        private static void TestMigration()
        {
            Console.WriteLine("Testing migration runner...")
;

            var dbPath = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location)!, "test-index.db");
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }

            var store = new SqliteIndexStore(dbPath);
            store.Open(dbPath);

            var initialVersion = store.GetCurrentSchemaVersion();
            Console.WriteLine($"Initial schema version: {initialVersion}");

            store.RunMigrations();

            var afterVersion = store.GetCurrentSchemaVersion();
            Console.WriteLine($"Schema version after migrations: {afterVersion}");

            store.RunMigrations();

            var secondVersion = store.GetCurrentSchemaVersion();
            Console.WriteLine($"Schema version after second run: {secondVersion}");

            if (afterVersion == 1 && secondVersion == 1)
            {
                Console.WriteLine("✓ Migration test passed: schema version is 1 and idempotent");
            }
            else
            {
                Console.WriteLine("✗ Migration test failed");
                Environment.Exit(1);
            }

            store.Close();
        }

        private static void ShowStatus()
        {
            if (_indexStore == null || !_indexStore.IsOpen)
            {
                Console.WriteLine("Index store is not open");
                return;
            }

            var version = _indexStore.GetCurrentSchemaVersion();
            Console.WriteLine($"Database schema version: {version}");
        }
    }
}