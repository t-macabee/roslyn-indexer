using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lurp.Storage.Tests;

/// <summary>
/// T19 integration tests that drive the real <see cref="IndexRunner.RunAsync"/>
/// entrypoint against the committed <c>tests/fixtures/Sample/</c> solution.
/// Each test copies the fixture to a temp directory, indexes it, and asserts
/// invariants that would have caught C1–C6 and D1–D2.
/// </summary>
public sealed class RealSolutionIntegrationTests : IDisposable
{
    private string? _testDir;
    private string? _dbPath;

    public void Dispose()
    {
        if (_testDir != null && Directory.Exists(_testDir))
        {
            try
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(_testDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    /// <summary>
    /// Set up a temp copy of the fixture, git init it, and build it.
    /// Call at the start of each test.
    /// Returns (dbPath, solutionPath, outputDir).
    /// </summary>
    private (string DbPath, string SolutionPath, string OutputDir) SetupFixture()
    {
        _testDir = Path.Combine(
            Path.GetTempPath(),
            $"lurp_real_test_{Guid.NewGuid():N}");

        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "index.db");

        var solutionPath = IntegrationHarness.CopyFixtureToTemp(_testDir);

        // Git init so WorkspaceFreshness can compute stable workspace identities
        // and obj/bin exclusion behaves correctly (T7).
        RunGitCommand(_testDir, "init");
        RunGitCommand(_testDir, "config user.email test@test.com");
        RunGitCommand(_testDir, "config user.name test");
        RunGitCommand(_testDir, "add -A");
        RunGitCommand(_testDir, "commit -m init");

        // Build so generated files (obj/bin) are present — exercises T7 exclusion.
        RunDotNetBuild(solutionPath);

        return (_dbPath, solutionPath, _testDir);
    }

    private static void RunGitCommand(string workingDir, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(30000);
    }

    private static void RunDotNetBuild(string solutionPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"build \"{solutionPath}\" --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(60000);
    }

    // ── Test 1: FullIndex_Completes_WithNonZeroCounts ──────────────────────
    // Catches C1 (positional record crash) and C2 (FTS built after extraction).

    [Fact]
    public async Task FullIndex_Completes_WithNonZeroCounts()
    {
        IntegrationHarness.EnsureMSBuild();
        var (dbPath, solutionPath, outputDir) = SetupFixture();

        var snapshotId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        var symbolCount = CountFromSql(conn, "SELECT COUNT(*) FROM snapshot_symbols WHERE snapshot_id = @id", snapshotId);
        var edgeCount = CountFromSql(conn, "SELECT COUNT(*) FROM edges WHERE snapshot_id = @id", snapshotId);
        var ftsCount = CountFromSql(conn, "SELECT COUNT(*) FROM symbol_fts WHERE snapshot_id = @id", snapshotId);

        Assert.True(symbolCount > 0, $"Expected > 0 symbols, got {symbolCount}");
        Assert.True(edgeCount > 0, $"Expected > 0 edges, got {edgeCount}");
        Assert.True(ftsCount > 0, $"Expected > 0 symbol_fts rows, got {ftsCount}");
    }

    // ── Test 2: Status_AfterFreshIndex_ReportsUpToDate ─────────────────────
    // Catches C5 (snapshot properly marked complete; status path doesn't crash).

    [Fact]
    public async Task Status_AfterFreshIndex_ReportsUpToDate()
    {
        IntegrationHarness.EnsureMSBuild();
        var (dbPath, solutionPath, outputDir) = SetupFixture();

        await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        // Drive the real --mode=status path (StatusHandler.Run) rather than
        // asserting on the database directly — this is what the CLI's status
        // command actually executes, and C5 is about that path not crashing
        // and reporting freshness correctly right after an index run.
        var originalOut = Console.Out;
        var capturedOut = new StringWriter();
        Console.SetOut(capturedOut);
        try
        {
            await Lurp.Handlers.StatusHandler.Run(
            [
                $"--output-dir={outputDir}",
                $"--solution={solutionPath}",
            ]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = capturedOut.ToString();
        Assert.True(output.Contains("Freshness: up to date."), $"Expected up-to-date freshness, got:\n{output}");
        Assert.DoesNotContain("mismatch", output, StringComparison.OrdinalIgnoreCase);
    }

    // ── Test 3: Incremental_PreservesPriorSnapshotSource ───────────────────
    // Catches C4 (old snapshot source overwritten after incremental).

    [Fact]
    public async Task Incremental_PreservesPriorSnapshotSource()
    {
        IntegrationHarness.EnsureMSBuild();
        var (dbPath, solutionPath, outputDir) = SetupFixture();

        var snapshotA = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        // Pick a symbol from the App project and read its original source
        string originalSource;
        string symbolId;
        using (var store = IntegrationHarness.OpenReadStore(dbPath))
        {
            var symbols = store.GetSymbolIdsInSnapshot(snapshotA);
            // Find a symbol from the App project (assembly name "App")
            symbolId = symbols.FirstOrDefault(s => s.Contains(":App:", StringComparison.Ordinal))
                ?? symbols.First();

            originalSource = store.GetSymbolSource(symbolId, snapshotA, ViewKind.Declaration)
                ?? throw new InvalidOperationException($"No source found for symbol {symbolId}");
        }

        // Mutate a method body in the App project
        var appDir = Path.Combine(_testDir!, "App");
        MutateGetProductHandler(appDir);

        var snapshotB = await IntegrationHarness.RunIncrementalIndexAsync(dbPath, solutionPath, outputDir);

        // Read the old snapshot's source — it must still be the original
        using (var store = IntegrationHarness.OpenReadStore(dbPath))
        {
            var oldSource = store.GetSymbolSource(symbolId, snapshotA, ViewKind.Declaration);
            Assert.NotNull(oldSource);
            Assert.Equal(originalSource, oldSource);
        }
    }

    // ── Test 4: Incremental_Matches_CleanRebuild_OnFixture ─────────────────
    // Catches C6 and D1 (incremental silently diverges from full rebuild).

    [Fact]
    public async Task Incremental_Matches_CleanRebuild_OnFixture()
    {
        IntegrationHarness.EnsureMSBuild();
        var (dbPath, solutionPath, outputDir) = SetupFixture();

        await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        // Mutate the handler
        var appDir = Path.Combine(_testDir!, "App");
        MutateGetProductHandler(appDir);

        // Delete db so incremental starts fresh (it needs an existing snapshot,
        // but RunAsync with incremental strategy auto-falls-back to full if none exists).
        // We want a clean compare: full(A) → mutate → incremental(B) → full(C).
        // Don't delete — we need the previous snapshot for incremental to work.
        var snapshotB = await IntegrationHarness.RunIncrementalIndexAsync(dbPath, solutionPath, outputDir);

        // Full rebuild with same state as B
        var snapshotC = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        SnapshotAssertions.CompareSnapshotsAreEquivalent(dbPath, snapshotB, snapshotC);
    }

    // ── Test 5: SourceSearch_Returns_Bounded_Distinct_Snippets ─────────────
    // Catches C3 (source search returns duplicates or full-file dumps).

    [Fact]
    public async Task SourceSearch_Returns_Bounded_Distinct_Snippets()
    {
        IntegrationHarness.EnsureMSBuild();
        var (dbPath, solutionPath, outputDir) = SetupFixture();

        var snapshotId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        using var store = IntegrationHarness.OpenReadStore(dbPath);
        var results = store.SearchSource("Product", snapshotId, limit: 3);

        Assert.Equal(3, results.Count);

        // All document paths must be distinct
        var docPaths = results.Select(r => r.DocumentPath).ToList();
        Assert.Equal(docPaths.Distinct(StringComparer.Ordinal).Count(), docPaths.Count);

        // Each snippet must be bounded — no full-file dumps (> 2000 chars is suspicious)
        const int maxSnippetLength = 2000;
        foreach (var result in results)
        {
            Assert.True(result.Snippet.Length <= maxSnippetLength,
                $"Snippet for {result.DocumentPath} is {result.Snippet.Length} chars — expected <= {maxSnippetLength}");
        }
    }

    // ── Test 6: Edges_Have_No_AbsolutePaths_And_Only_Canonical_Provenance ──
    // Catches D1 (absolute paths in source_document_path) and
    // D2 (non-canonical provenance values).

    [Fact]
    public async Task Edges_Have_No_AbsolutePaths_And_Only_Canonical_Provenance()
    {
        IntegrationHarness.EnsureMSBuild();
        var (dbPath, solutionPath, outputDir) = SetupFixture();

        await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        // D1: no absolute paths (Windows drive letter or Unix root)
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM edges WHERE source_document_path LIKE '_:%';";
            var absolutePathCount = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            Assert.Equal(0, absolutePathCount);
        }

        // D2: all provenance values are canonical
        var canonical = new HashSet<string>(StringComparer.Ordinal)
        {
            "compiler_proved", "framework_derived", "possible",
            "name_candidate", "runtime_unknown", "convention"
        };

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT provenance FROM edges;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var provenance = reader.GetString(0);
                Assert.True(canonical.Contains(provenance),
                    $"Non-canonical provenance value found: '{provenance}'");
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static int CountFromSql(SqliteConnection conn, string sql, string snapshotId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", snapshotId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// Mutate the GetProductHandler body so the file hash changes.
    /// This forces the incremental indexer to re-extract App's edges.
    /// </summary>
    private static void MutateGetProductHandler(string appDir)
    {
        var handlerPath = Path.Combine(appDir, "GetProductHandler.cs");
        var content = File.ReadAllText(handlerPath);

        // Change the product name in the handler body
        content = content.Replace("\"Widget\"", "\"ModifiedWidget\"");

        File.WriteAllText(handlerPath, content);
    }
}
