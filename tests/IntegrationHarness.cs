using System;
using System.IO;
using System.Threading.Tasks;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;

namespace Lurp.Storage.Tests;

/// <summary>
/// Harness that drives the real <see cref="IndexRunner.RunAsync"/> entrypoint
/// against a committed fixture solution. This is the point of T19 — previous
/// integration tests hand-rolled the pipeline (CompilationFactExtractor + save
/// calls) and never exercised the CLI's actual code path.
/// </summary>
public static class IntegrationHarness
{
    /// <summary>
    /// Resolve the committed fixture root relative to the test assembly location.
    /// </summary>
    public static string GetFixtureRoot()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(IntegrationHarness).Assembly.Location)
            ?? throw new InvalidOperationException("Cannot determine test assembly location.");

        return Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "fixtures", "Sample"));
    }

    /// <summary>
    /// Copy the committed fixture to a temp directory and return the path to the .slnx.
    /// </summary>
    public static string CopyFixtureToTemp(string testDir)
    {
        var fixtureRoot = GetFixtureRoot();
        CopyDirectory(fixtureRoot, testDir);
        return Path.Combine(testDir, "Sample.slnx");
    }

    /// <summary>
    /// Run a full index through <see cref="IndexRunner.RunAsync"/> and return the snapshot id.
    /// </summary>
    public static async Task<string> RunFullIndexAsync(string dbPath, string solutionPath, string outputDir)
    {
        var store = CreateAndOpenStore(dbPath);

        try
        {
            await IndexRunner.RunAsync(
                store,
                solutionPath,
                outputDir,
                skipAdapters: [],
                jsonExportPath: null,
                strategyArg: "full");

            return store.GetLatestSnapshotId()
                ?? throw new InvalidOperationException("Full index completed but no snapshot id was returned.");
        }
        finally
        {
            store.Close();
        }
    }

    /// <summary>
    /// Run an incremental index through <see cref="IndexRunner.RunAsync"/> and return the snapshot id.
    /// </summary>
    public static async Task<string> RunIncrementalIndexAsync(string dbPath, string solutionPath, string outputDir)
    {
        var store = CreateAndOpenStore(dbPath);

        try
        {
            await IndexRunner.RunAsync(
                store,
                solutionPath,
                outputDir,
                skipAdapters: [],
                jsonExportPath: null,
                strategyArg: "incremental");

            return store.GetLatestSnapshotId()
                ?? throw new InvalidOperationException("Incremental index completed but no snapshot id was returned.");
        }
        finally
        {
            store.Close();
        }
    }

    /// <summary>
    /// Open a store for read-only queries (raw SQL assertions, snapshot comparisons).
    /// </summary>
    public static SqliteIndexStore OpenReadStore(string dbPath)
    {
        var store = new SqliteIndexStore(dbPath);
        store.Open(dbPath);
        store.RunMigrations();
        return store;
    }

    /// <summary>
    /// Guard: throws SkipException when MSBuild is not available.
    /// Call this at the start of every integration test.
    /// </summary>
    public static void EnsureMSBuild()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            try
            {
                MSBuildLocator.RegisterDefaults();
            }
            catch
            {
                throw new SkipException("MSBuild is not available on this system. Cannot run integration test.");
            }
        }
    }

    private static SqliteIndexStore CreateAndOpenStore(string dbPath)
    {
        var store = new SqliteIndexStore(dbPath);
        store.Open(dbPath);
        store.RunMigrations();
        return store;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
            CopyDirectory(dir, destSubDir);
        }
    }
}
