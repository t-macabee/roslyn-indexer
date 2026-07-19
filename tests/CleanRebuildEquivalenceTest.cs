using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Lurp;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Build.Locator;
using Xunit;

namespace Lurp.Storage.Tests;

public sealed class CleanRebuildEquivalenceTest : IAsyncLifetime, IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly string _solutionPath;

    public CleanRebuildEquivalenceTest()
    {
        _testDir = Path.Combine(
            Path.GetTempPath(),
            $"lurp_equiv_test_{Guid.NewGuid():N}");

        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "index.db");
        _solutionPath = Path.Combine(_testDir, "TestSolution.slnx");
    }

    public async Task InitializeAsync()
    {

        if (!MSBuildLocator.IsRegistered)
        {
            try
            {
                MSBuildLocator.RegisterDefaults();
            }
            catch
            {

            }
        }

        CreateTestSolution();
    }

    public Task DisposeAsync()
    {

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        SqliteConnectionClearAllPools();
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task IncrementalIndex_Matches_FullRebuild_AfterSingleFileChange()
    {

        if (!MSBuildLocator.IsRegistered)
        {
            throw new SkipException("MSBuild is not available on this system. Cannot run integration test.");
        }

        var snapshotA = await RunFullIndexAsync("Index A (full initial)");

        ModifyOneFile();

        var snapshotB = await RunIncrementalIndexAsync("Index B (incremental)");

        var snapshotC = await RunFullIndexAsync("Index C (full after change)", deleteFirst: false);

        CompareSnapshotsAreEquivalent(snapshotB, snapshotC);
    }

    // Regression test for a scoping gap in DeleteEdgesWithNullDocumentPathForAssemblies:
    // edges sourced from symbols with no DeclaringSyntaxReferences (e.g. an
    // implicit default constructor) carry a NULL source_document_path, which
    // can't be scoped to a project by path. If the incremental indexer
    // deletes all null-path edges snapshot-wide instead of only those
    // belonging to re-extracted projects, an untouched project's null-path
    // edges are deleted and never regenerated, causing the incremental
    // snapshot to silently lose edges relative to a full rebuild.
    [Fact]
    public async Task IncrementalIndex_Matches_FullRebuild_WhenUnaffectedProjectHasImplicitMembers()
    {

        if (!MSBuildLocator.IsRegistered)
        {
            throw new SkipException("MSBuild is not available on this system. Cannot run integration test.");
        }

        CreateMultiProjectTestSolution();

        var snapshotA = await RunFullIndexAsync("Index A (full initial, multi-project)");

        ModifyFileInProjectB();

        var snapshotB = await RunIncrementalIndexAsync("Index B (incremental, only ProjectB touched)");

        var snapshotC = await RunFullIndexAsync("Index C (full after change)", deleteFirst: false);

        CompareSnapshotsAreEquivalent(snapshotB, snapshotC);
    }

    private async Task<string> RunFullIndexAsync(string label, bool deleteFirst = true)
    {
        Console.WriteLine($"--- {label} ---");

        if (deleteFirst && File.Exists(_dbPath))
            File.Delete(_dbPath);

        var store = new SqliteIndexStore(_dbPath);
        store.Open(_dbPath);
        store.RunMigrations();

        try
        {
            using var workspace = MSBuildWorkspace.Create();
            workspace.RegisterWorkspaceFailedHandler(args =>
            {

                Console.Error.WriteLine($"  [Workspace] {args.Diagnostic.Kind}: {args.Diagnostic.Message}");
            });

            var solution = await workspace.OpenSolutionAsync(_solutionPath);
            var gitRoot = _testDir;
            var workspaceInfo = new WorkspaceInfo(solution, gitRoot);

            var snapshotId = SnapshotId.New();
            var manifest = global::Lurp.Workspace.SnapshotManifest.FromWorkspace(workspaceInfo, snapshotId);
            var snapshotIdStr = snapshotId.ToString();

            manifest.Save(store, store, workspaceInfo.DocumentContents, jsonExportPath: null);

            int totalDeclarations = 0;
            int totalEdges = 0;
            int totalDiagnostics = 0;

            foreach (var (project, compilation) in await GetAllAsync(solution))
            {
                var projectName = project.Name;
                Console.WriteLine($"    [{projectName}]");

                var result = CompilationFactExtractor.ExtractAll(
                    compilation, workspaceInfo, snapshotIdStr, projectName,
                    skipAdapters: new HashSet<string>());

                store.SaveDeclarations(snapshotIdStr, result.Declarations);
                totalDeclarations += result.Declarations.Count;

                store.SaveEdges(snapshotIdStr, result.Edges);
                totalEdges += result.Edges.Count;

                store.SaveDiagnostics(snapshotIdStr, result.Diagnostics);
                totalDiagnostics += result.Diagnostics.Count;

                Console.WriteLine($"      {result.Declarations.Count} symbols, {result.Edges.Count} edges, {result.Diagnostics.Count} diagnostics.");
            }

            var previousManifest = store.LoadLatestSnapshot(manifest.WorkspaceId.Value);
            if (previousManifest != null && previousManifest.SnapshotId != snapshotIdStr)
            {
                var differ = new Workspace.SemanticDiffer(store, store, store);
                var diffChanges = differ.ComputeDiff(previousManifest.SnapshotId, snapshotIdStr);
                store.SaveSemanticChanges(previousManifest.SnapshotId, snapshotIdStr, diffChanges);
            }

            Console.WriteLine($"    Snapshot: {snapshotIdStr}");
            return snapshotIdStr;
        }
        finally
        {
            store.PruneOldSnapshots(keep: 3);
            store.Close();
        }
    }

    private async Task<string> RunIncrementalIndexAsync(string label)
    {
        Console.WriteLine($"--- {label} ---");

        var store = new SqliteIndexStore(_dbPath);
        store.Open(_dbPath);
        store.RunMigrations();

        try
        {
            using var workspace = MSBuildWorkspace.Create();
            workspace.RegisterWorkspaceFailedHandler(args =>
            {
                Console.Error.WriteLine($"  [Workspace] {args.Diagnostic.Kind}: {args.Diagnostic.Message}");
            });

            var solution = await workspace.OpenSolutionAsync(_solutionPath);
            var gitRoot = _testDir;
            var workspaceInfo = new WorkspaceInfo(solution, gitRoot);

            var previousManifest = store.LoadLatestSnapshot(workspaceInfo.Id.Value);

            if (previousManifest == null)
                throw new InvalidOperationException("No previous snapshot found. Cannot run incremental index.");

            var incrementalIndexer = new IncrementalIndexer(
                store, gitRoot, _solutionPath, _testDir,
                skipAdapters: [],
                jsonExportPath: null);

            var result = await incrementalIndexer.RunIncrementalAsync(
                solution, workspaceInfo, previousManifest);

            Console.WriteLine($"    New snapshot: {result.NewSnapshotId}");
            return result.NewSnapshotId;
        }
        finally
        {
            store.PruneOldSnapshots(keep: 3);
            store.Close();
        }
    }

    private void CompareSnapshotsAreEquivalent(string snapshotB, string snapshotC)
    {
        Assert.NotEqual(snapshotB, snapshotC);

        var store = new SqliteIndexStore(_dbPath);
        store.Open(_dbPath);

        try
        {

            var symbolsB = store.GetSymbolIdsInSnapshot(snapshotB);
            var symbolsC = store.GetSymbolIdsInSnapshot(snapshotC);
            symbolsB.Sort(StringComparer.Ordinal);
            symbolsC.Sort(StringComparer.Ordinal);

            Assert.Equal(symbolsC.Count, symbolsB.Count);
            Assert.True(
                symbolsB.SequenceEqual(symbolsC, StringComparer.Ordinal),
                $"Symbol set mismatch between incremental (B) and full rebuild (C).\n" +
                $"  B count: {symbolsB.Count}, C count: {symbolsC.Count}\n" +
                $"  Only in B: {string.Join(", ", symbolsB.Except(symbolsC, StringComparer.Ordinal).Take(10))}\n" +
                $"  Only in C: {string.Join(", ", symbolsC.Except(symbolsB, StringComparer.Ordinal).Take(10))}");

            var edgesB = store.GetEdges(snapshotB);
            var edgesC = store.GetEdges(snapshotC);
            NormalizeEdges(edgesB);
            NormalizeEdges(edgesC);

            Assert.Equal(edgesC.Count, edgesB.Count);
            for (int i = 0; i < edgesC.Count && i < edgesB.Count; i++)
            {
                AssertEqual(edgesB[i], edgesC[i]);
            }
            if (edgesC.Count != edgesB.Count)
            {
                var bSet = edgesB.Select(e => $"{e.SourceSymbolId}|{e.TargetSymbolId}|{e.Kind}|{e.Provenance}").ToHashSet();
                var cSet = edgesC.Select(e => $"{e.SourceSymbolId}|{e.TargetSymbolId}|{e.Kind}|{e.Provenance}").ToHashSet();
                Assert.Fail($"Edge count mismatch: {edgesB.Count} vs {edgesC.Count}.\n" +
                    $"Only in B: {string.Join(", ", bSet.Except(cSet).Take(10))}\n" +
                    $"Only in C: {string.Join(", ", cSet.Except(bSet).Take(10))}");
            }

            var diagB = store.GetDiagnostics(snapshotB);
            var diagC = store.GetDiagnostics(snapshotC);
            NormalizeDiagnostics(diagB);
            NormalizeDiagnostics(diagC);

            Assert.Equal(diagC.Count, diagB.Count);
            for (int i = 0; i < diagC.Count && i < diagB.Count; i++)
            {
                AssertEqual(diagB[i], diagC[i]);
            }

            var annB = store.GetAnnotations(snapshotB);
            var annC = store.GetAnnotations(snapshotC);
            NormalizeAnnotations(annB);
            NormalizeAnnotations(annC);

            Assert.Equal(annC.Count, annB.Count);
            for (int i = 0; i < annC.Count && i < annB.Count; i++)
            {
                AssertEqual(annB[i], annC[i]);
            }

            Console.WriteLine("    Symbols, edges, diagnostics, annotations: all match.");
        }
        finally
        {
            store.Close();
        }
    }

    private void CreateTestSolution()
    {

        var projDir = Path.Combine(_testDir, "src", "TestProject");
        Directory.CreateDirectory(projDir);

        var csprojPath = Path.Combine(projDir, "TestProject.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        File.WriteAllText(
            Path.Combine(projDir, "Calculator.cs"),
            """
            namespace TestProject;

            public class Calculator
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }

                public int Subtract(int a, int b)
                {
                    return a - b;
                }
            }
            """);

        File.WriteAllText(
            Path.Combine(projDir, "Service.cs"),
            """
            namespace TestProject;

            public class Service
            {
                private readonly Calculator _calculator;

                public Service()
                {
                    _calculator = new Calculator();
                }

                public int Compute(int x, int y)
                {
                    return _calculator.Add(x, y);
                }
            }
            """);

        File.WriteAllText(
            Path.Combine(projDir, "Models.cs"),
            """
            namespace TestProject;

            public class User
            {
                public string Name { get; set; } = "";
                public int Age { get; set; }
            }

            public class Product
            {
                public string Id { get; set; } = "";
                public decimal Price { get; set; }
            }
            """);

        File.WriteAllText(_solutionPath, $"""
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/TestProject/TestProject.csproj" />
              </Folder>
            </Solution>
            """);
    }

    private void ModifyOneFile()
    {
        var calculatorPath = Path.Combine(_testDir, "src", "TestProject", "Calculator.cs");
        File.WriteAllText(calculatorPath,
            """
            namespace TestProject;

            public class Calculator
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }

                public int Subtract(int a, int b)
                {
                    return a - b;
                }

                // Added method
                public int Multiply(int a, int b)
                {
                    return a * b;
                }
            }
            """);
    }

    private void CreateMultiProjectTestSolution()
    {
        var projADir = Path.Combine(_testDir, "src", "ProjectA");
        Directory.CreateDirectory(projADir);

        File.WriteAllText(Path.Combine(projADir, "ProjectA.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        // No explicit constructor: Roslyn synthesizes an implicit parameterless
        // constructor with no DeclaringSyntaxReferences, so the edge sourced from
        // it has a NULL source_document_path. ProjectA is never touched by the
        // incremental run in the test below, so its edges must survive unchanged.
        File.WriteAllText(Path.Combine(projADir, "Widgets.cs"), """
            namespace ProjectA;

            public class Widget
            {
                public string Name { get; set; } = "";
            }

            public class Gadget
            {
                public int Count { get; set; }
            }
            """);

        var projBDir = Path.Combine(_testDir, "src", "ProjectB");
        Directory.CreateDirectory(projBDir);

        File.WriteAllText(Path.Combine(projBDir, "ProjectB.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        File.WriteAllText(Path.Combine(projBDir, "Calculator.cs"), """
            namespace ProjectB;

            public class Calculator
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """);

        File.WriteAllText(_solutionPath, $"""
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/ProjectA/ProjectA.csproj" />
                <Project Path="src/ProjectB/ProjectB.csproj" />
              </Folder>
            </Solution>
            """);
    }

    private void ModifyFileInProjectB()
    {
        var calculatorPath = Path.Combine(_testDir, "src", "ProjectB", "Calculator.cs");
        File.WriteAllText(calculatorPath,
            """
            namespace ProjectB;

            public class Calculator
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }

                // Added method
                public int Multiply(int a, int b)
                {
                    return a * b;
                }
            }
            """);
    }

    private static void NormalizeEdges(List<EdgeRecord> edges)
    {
        // Normalize snapshot ID so edges from different snapshots can be compared.
        foreach (var edge in edges)
        {
            var field = typeof(EdgeRecord).GetField("<SnapshotId>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field != null)
                field.SetValue(edge, string.Empty);
        }

        edges.Sort((a, b) =>
        {
            int cmp = StringComparer.Ordinal.Compare(a.SourceSymbolId, b.SourceSymbolId);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.TargetSymbolId, b.TargetSymbolId);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.Kind, b.Kind);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.Provenance, b.Provenance);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.SourceDocumentPath ?? "", b.SourceDocumentPath ?? "");
            if (cmp != 0) return cmp;
            return (a.SourceStartLine ?? 0).CompareTo(b.SourceStartLine ?? 0);
        });
    }

    private static void NormalizeDiagnostics(List<DiagnosticRecord> diags)
    {
        diags.Sort((a, b) =>
        {
            int cmp = StringComparer.Ordinal.Compare(a.DocumentPath ?? "", b.DocumentPath ?? "");
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.Id, b.Id);
            if (cmp != 0) return cmp;
            return (a.StartLine ?? 0).CompareTo(b.StartLine ?? 0);
        });
    }

    private static void NormalizeAnnotations(List<AnnotationRecord> annotations)
    {
        annotations.Sort((a, b) =>
        {
            int cmp = StringComparer.Ordinal.Compare(a.SymbolId, b.SymbolId);
            if (cmp != 0) return cmp;
            return StringComparer.Ordinal.Compare(a.Kind, b.Kind);
        });
    }

    private static void AssertEqual(EdgeRecord expected, EdgeRecord actual)
    {
        Assert.Equal(expected.SourceSymbolId, actual.SourceSymbolId);
        Assert.Equal(expected.TargetSymbolId, actual.TargetSymbolId);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Provenance, actual.Provenance);
        Assert.Equal(expected.SnapshotId ?? "", actual.SnapshotId ?? "");
        Assert.Equal(expected.ExtractorVersion ?? "", actual.ExtractorVersion ?? "");
        Assert.Equal(expected.SourceDocumentPath ?? "", actual.SourceDocumentPath ?? "");
        Assert.Equal(expected.SourceStartLine, actual.SourceStartLine);
        Assert.Equal(expected.SourceEndLine, actual.SourceEndLine);
        Assert.Equal(expected.SourceStartColumn, actual.SourceStartColumn);
        Assert.Equal(expected.SourceEndColumn, actual.SourceEndColumn);
    }

    private static void AssertEqual(DiagnosticRecord expected, DiagnosticRecord actual)
    {
        Assert.Equal(expected.ProjectName, actual.ProjectName);
        Assert.Equal(expected.DocumentPath, actual.DocumentPath);
        Assert.Equal(expected.Severity, actual.Severity);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Message, actual.Message);
        Assert.Equal(expected.StartLine, actual.StartLine);
        Assert.Equal(expected.StartColumn, actual.StartColumn);
        Assert.Equal(expected.EndLine, actual.EndLine);
        Assert.Equal(expected.EndColumn, actual.EndColumn);
    }

    private static void AssertEqual(AnnotationRecord expected, AnnotationRecord actual)
    {
        Assert.Equal(expected.SymbolId, actual.SymbolId);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Value, actual.Value);
    }

    private static async Task<List<(Project Project, Compilation Compilation)>> GetAllAsync(Solution solution)
    {
        var results = new List<(Project Project, Compilation Compilation)>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation != null)
                results.Add((project, compilation));
        }
        return results;
    }

    private static void SqliteConnectionClearAllPools()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        catch
        {

        }
    }
}

internal sealed class SkipException : Exception
{
    public SkipException(string message) : base(message) { }
}
