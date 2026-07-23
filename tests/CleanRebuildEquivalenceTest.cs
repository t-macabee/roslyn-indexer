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
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lurp.Storage.Tests;

public sealed class PipelineEquivalenceTest : IAsyncLifetime, IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly string _solutionPath;

    public PipelineEquivalenceTest()
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

            manifest.Save(store, workspaceInfo.DocumentContents, jsonExportPath: null);

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
                var (diffChanges, _) = differ.ComputeDiff(previousManifest.SnapshotId, snapshotIdStr);
                store.SaveSemanticChanges(previousManifest.SnapshotId, snapshotIdStr, diffChanges);
            }

            store.BuildSearchIndex(snapshotIdStr);
            store.MarkSnapshotComplete(snapshotIdStr);

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
        SnapshotAssertions.CompareSnapshotsAreEquivalent(_dbPath, snapshotB, snapshotC);
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

    // T16a: Regression test for CrossDocumentEdgeRefresher — verifies that
    // when a symbol in ProjectA changes, documents in ProjectB that reference
    // the changed symbol through cross-project edges have their edges
    // correctly re-extracted during incremental indexing. Without the
    // CrossDocumentEdgeRefresher, edges sourced from ProjectB documents
    // that point at changed ProjectA symbols would be stale (copied forward
    // from the previous snapshot with no re-extraction), and the incremental
    // snapshot would silently diverge from a full rebuild.
    [Fact]
    public async Task IncrementalIndex_Matches_FullRebuild_WhenDependentProjectReferencesChangedSymbol()
    {

        if (!MSBuildLocator.IsRegistered)
        {
            throw new SkipException("MSBuild is not available on this system. Cannot run integration test.");
        }

        CreateCrossProjectDependentTestSolution();

        var snapshotA = await RunFullIndexAsync("Index A (full initial, cross-project)");

        ModifyLibraryFile();

        var snapshotB = await RunIncrementalIndexAsync("Index B (incremental, Library changed)");

        var snapshotC = await RunFullIndexAsync("Index C (full after Library change)", deleteFirst: false);

        CompareSnapshotsAreEquivalent(snapshotB, snapshotC);
    }

    private void CreateCrossProjectDependentTestSolution()
    {
        // ProjectA: Library — defines Widget (the type that will change)
        var libDir = Path.Combine(_testDir, "src", "Library");
        Directory.CreateDirectory(libDir);

        File.WriteAllText(Path.Combine(libDir, "Library.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        File.WriteAllText(Path.Combine(libDir, "Widget.cs"), """
            namespace Library;

            public class Widget
            {
                public string Name { get; set; } = "";

                public string GetLabel()
                {
                    return Name;
                }
            }
            """);

        // ProjectB: App — references Library and uses Widget
        var appDir = Path.Combine(_testDir, "src", "App");
        Directory.CreateDirectory(appDir);

        File.WriteAllText(Path.Combine(appDir, "App.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include=""..\Library\Library.csproj"" />
  </ItemGroup>
</Project>");

        File.WriteAllText(Path.Combine(appDir, "Calculator.cs"), """
            namespace App;

            public class Calculator
            {
                private readonly Library.Widget _widget;

                public Calculator(Library.Widget widget)
                {
                    _widget = widget;
                }

                public string GetWidgetName()
                {
                    return _widget.GetLabel();
                }
            }
            """);

        File.WriteAllText(_solutionPath, $"""
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/Library/Library.csproj" />
                <Project Path="src/App/App.csproj" />
              </Folder>
            </Solution>
            """);
    }

    private void ModifyLibraryFile()
    {
        var widgetPath = Path.Combine(_testDir, "src", "Library", "Widget.cs");
        File.WriteAllText(widgetPath, """
            namespace Library;

            public class Widget
            {
                public string Name { get; set; } = "";

                public string GetLabel()
                {
                    return Name;
                }

                // Added method — forces the Library document to change
                public int GetNameLength()
                {
                    return Name.Length;
                }
            }
            """);
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
        SnapshotAssertions.SqliteConnectionClearAllPools();
    }

    private (int SourceRows, int SymbolRows) GetFtsCounts(string snapshotId)
    {
        return SnapshotAssertions.GetFtsCounts(_dbPath, snapshotId);
    }
}

internal sealed class SkipException : Exception
{
    public SkipException(string message) : base(message) { }
}
