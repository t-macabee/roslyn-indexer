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
            _indexStore.ValidateSchema(expectedVersion: 1);

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