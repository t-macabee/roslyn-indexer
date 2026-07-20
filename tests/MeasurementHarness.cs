using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Build.Locator;
using Xunit;
using Xunit.Abstractions;

namespace Lurp.Storage.Tests;

/// <summary>
/// Measurement harness that generates a realistically-sized multi-project solution,
/// runs a clean full rebuild, then applies a matrix of representative edits and
/// records incremental cost per edit type vs. clean-rebuild cost.
///
/// Run with: dotnet test --filter "MeasurementHarness"
///
/// This harness is the measurement tool described in Phase 17 of the architecture doc.
/// It separates the concern of generating measurement data from the timing capture
/// infrastructure itself (which is embedded in the main indexing pipeline).
/// </summary>
public sealed class MeasurementHarness : IAsyncLifetime, IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly string _solutionPath;
    private readonly ITestOutputHelper _output;

    public MeasurementHarness(ITestOutputHelper output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _testDir = Path.Combine(
            Path.GetTempPath(),
            $"lurp_measure_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "index.db");
        _solutionPath = Path.Combine(_testDir, "MeasurementSolution.slnx");
    }

    public async Task InitializeAsync()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            try { MSBuildLocator.RegisterDefaults(); }
            catch { }
        }

        CreateRealisticSolution();
        await Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

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
    public async Task MeasureAllEditTypes()
    {
        if (!MSBuildLocator.IsRegistered)
            throw new SkipException("MSBuild not available.");

        // ── Phase 1: Clean rebuild (baseline) ─────────────────
        _output.WriteLine("");
        _output.WriteLine("═══ Phase 1: Clean Rebuild (Baseline) ═══");
        var baselineSnapshotId = await RunFullIndexAsync("Clean full rebuild");

        var baselineTimings = ReadTimings(baselineSnapshotId);
        var baselineTotal = baselineTimings.Sum(t => t.ElapsedMs);
        _output.WriteLine($"  Baseline total: {baselineTotal} ms");

        // ── Phase 2: Edit matrix ─────────────────────────────
        _output.WriteLine("");
        _output.WriteLine("═══ Phase 2: Incremental Edit Matrix ═══");
        _output.WriteLine("");

        var results = new List<(string EditType, string SnapshotId, long TotalMs, List<SnapshotTimingRow> Timings)>();

        // Each edit is applied from baseline, then reverted after measurement.
        // This ensures only the single intended change is measured, not solution regeneration noise.
        var edits = new (string EditType, string Description, Action Apply, Action Revert)[]
        {
            ("method_body_edit", "Method body edit (Add in Calculator changed)",
                ApplyMethodBodyEdit, RevertMethodBodyEdit),
            ("signature_change", "Signature change (Subtract gets new parameter)",
                ApplySignatureChange, RevertSignatureChange),
            ("new_type", "New type added",
                ApplyNewType, RevertNewType),
            ("deleted_type", "Type deleted",
                ApplyDeleteType, RevertDeleteType),
            ("cross_project_interface_change", "Cross-project interface changed",
                ApplyInterfaceChange, RevertInterfaceChange),
        };

        foreach (var (editType, description, applyEdit, revertEdit) in edits)
        {
            _output.WriteLine($"--- Edit: {editType} ({description}) ---");
            applyEdit();

            var snapshotId = await RunIncrementalIndexAsync($"Incremental after {editType}");
            var timings = ReadTimings(snapshotId);
            var totalMs = timings.Sum(t => t.ElapsedMs);
            results.Add((editType, snapshotId, totalMs, timings));

            _output.WriteLine($"  Total: {totalMs} ms ({totalMs * 100.0 / Math.Max(baselineTotal, 1):F1}% of baseline)");
            _output.WriteLine("");

            revertEdit();
        }

        // ── Phase 3: Report ──────────────────────────────────
        _output.WriteLine("");
        _output.WriteLine("═══ Phase 3: Comparison Report ═══");
        _output.WriteLine("");
        _output.WriteLine($"{"Edit Type",-35} {"Total (ms)",-12} {"% Baseline",-12}");
        _output.WriteLine(new string('-', 60));
        _output.WriteLine($"{"BASELINE (clean rebuild)",-35} {baselineTotal,12} {"100.0%",-12}");
        foreach (var (editType, _, totalMs, _) in results)
        {
            _output.WriteLine($"{editType,-35} {totalMs,12} {totalMs * 100.0 / Math.Max(baselineTotal, 1),10:F1}%");
        }
        _output.WriteLine(new string('-', 60));

        // Per-step breakdown for each edit
        _output.WriteLine("");
        _output.WriteLine("Per-step breakdown (ms):");
        _output.WriteLine("");

        // Collect all unique step names
        var allSteps = new List<string>();
        foreach (var (_, _, _, timings) in results)
            foreach (var t in timings)
                if (!allSteps.Contains(t.StepName))
                    allSteps.Add(t.StepName);

        // Header
        var header = $"{"Step",-30}";
        foreach (var (editType, _, _, _) in results)
            header += $" {editType,-22}";
        _output.WriteLine(header);

        foreach (var stepName in allSteps)
        {
            var line = $"{stepName,-30}";
            foreach (var (_, _, _, timings) in results)
            {
                var step = timings.FirstOrDefault(t => t.StepName == stepName);
                line += $" {step?.ElapsedMs.ToString() ?? "-",22}";
            }
            _output.WriteLine(line);
        }

        // Assertion: every edit should produce a valid incremental result
        Assert.True(results.All(r => r.TotalMs > 0), "All edits should produce timing data");
    }

    // ── Solution generation (realistic scale) ───────────────

    private void CreateRealisticSolution()
    {
        // 5 projects, ~30 files each, with cross-project references
        var projectNames = new[] { "Core", "Services", "Data", "Api", "Tests" };
        var projectPaths = new Dictionary<string, string>();

        foreach (var name in projectNames)
        {
            var projDir = Path.Combine(_testDir, "src", name);
            Directory.CreateDirectory(projDir);
            projectPaths[name] = projDir;

            var refs = new List<string>();
            if (name == "Services") refs.Add("../Core/Core.csproj");
            if (name == "Data") { refs.Add("../Core/Core.csproj"); refs.Add("../Services/Services.csproj"); }
            if (name == "Api") { refs.Add("../Core/Core.csproj"); refs.Add("../Services/Services.csproj"); refs.Add("../Data/Data.csproj"); }
            if (name == "Tests") { refs.Add("../Core/Core.csproj"); refs.Add("../Services/Services.csproj"); refs.Add("../Data/Data.csproj"); refs.Add("../Api/Api.csproj"); }

            var refXml = string.Join("\n", refs.Select(r => $"    <ProjectReference Include=\"{r}\" />"));
            File.WriteAllText(Path.Combine(projDir, $"{name}.csproj"), $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
{refXml}
  </ItemGroup>
</Project>");

            GenerateProjectFiles(name, projDir, projectNames);
        }

        // Solution file
        var slnContent = "<Solution>\n";
        slnContent += "  <Folder Name=\"/src/\">\n";
        foreach (var name in projectNames)
        {
            slnContent += $"    <Project Path=\"src/{name}/{name}.csproj\" />\n";
        }
        slnContent += "  </Folder>\n";
        slnContent += "</Solution>";
        File.WriteAllText(_solutionPath, slnContent);
    }

    private void GenerateProjectFiles(string projectName, string projDir, string[] allProjects)
    {
        var rng = new Random(projectName.GetHashCode());
        var fileCount = projectName switch
        {
            "Core" => 40,
            "Services" => 35,
            "Data" => 30,
            "Api" => 25,
            "Tests" => 20,
            _ => 20,
        };

        var types = new List<(string Name, string Kind)>();

        // Generate interfaces
        for (int i = 0; i < fileCount / 4; i++)
        {
            var ifaceName = $"I{projectName}Interface{i}";
            types.Add((ifaceName, "interface"));

            var methods = new List<string>();
            for (int m = 0; m < 2 + rng.Next(3); m++)
            {
                var returnType = rng.Next(4) switch { 0 => "void", 1 => "int", 2 => "string", _ => "bool" };
                var methodName = $"Method{m}";
                var parms = GenerateParameters(rng, 0, 3 + rng.Next(3));
                methods.Add($"    {returnType} {methodName}({parms});");
            }

            File.WriteAllText(Path.Combine(projDir, $"{ifaceName}.cs"), $@"namespace {projectName};

public interface {ifaceName}
{{
{string.Join("\n\n", methods)}
}}");
        }

        // Generate classes
        for (int i = 0; i < fileCount * 3 / 4; i++)
        {
            var className = $"{projectName}Class{i}";
            types.Add((className, "class"));

            var implements = rng.Next(3) == 0 && i < fileCount / 4
                ? $" : I{projectName}Interface{i}"
                : "";

            var fields = new List<string>();
            var methods = new List<string>();
            var properties = new List<string>();

            // Fields
            for (int f = 0; f < 1 + rng.Next(3); f++)
            {
                var fieldType = rng.Next(4) switch { 0 => "int", 1 => "string", 2 => "bool", _ => "double" };
                fields.Add($"    private {fieldType} _field{f};");
            }

            // Properties
            for (int p = 0; p < 1 + rng.Next(3); p++)
            {
                var propType = rng.Next(4) switch { 0 => "int", 1 => "string", 2 => "bool", _ => "double" };
                var propName = $"Prop{p}";
                properties.Add($"    public {propType} {propName} {{ get; set; }}");
            }

            // Methods
            for (int m = 0; m < 2 + rng.Next(4); m++)
            {
                var returnType = rng.Next(5) switch { 0 => "void", 1 => "int", 2 => "string", 3 => "bool", _ => $"{projectName}Class{rng.Next(i)}" };
                var methodName = m == 0 ? $"Do{projectName}Work{m}" : $"Calculate{m}";
                var parms = GenerateParameters(rng, 0, 2 + rng.Next(3));
                var bodyLines = new List<string>();
                bodyLines.Add("    {");

                // Generate a realistic method body that references fields, properties, and calls other methods
                if (fields.Count > 0)
                    bodyLines.Add($"        var local = _field{rng.Next(fields.Count)};");
                if (properties.Count > 0)
                    bodyLines.Add($"        var val = Prop{rng.Next(properties.Count)};");
                if (returnType != "void")
                    bodyLines.Add($"        return default;");
                bodyLines.Add("    }");

                methods.Add($"    public {returnType} {methodName}({parms})\n{string.Join("\n", bodyLines)}");
            }

            // Cross-project references (for realistic edges)
            string crossRefUsings = "";
            if (projectName != allProjects[0] && rng.Next(3) == 0)
            {
                var refProject = allProjects[rng.Next(Array.IndexOf(allProjects, projectName))];
                crossRefUsings = $"using {refProject};\n";
            }

            File.WriteAllText(Path.Combine(projDir, $"{className}.cs"), $@"{crossRefUsings}namespace {projectName};

public class {className}{implements}
{{
{string.Join("\n\n", fields)}
{string.Join("\n\n", properties)}
{string.Join("\n\n", methods)}
}}");
        }

        // Generate cross-project interface for edge testing
        if (projectName == "Core")
        {
            File.WriteAllText(Path.Combine(projDir, "ICrossProjectService.cs"), @"namespace Core;

public interface ICrossProjectService
{
    int ProcessData(string input, int count);
    bool ValidateInput(string input);
    string TransformResult(int value);
}");
        }

        if (projectName == "Services")
        {
            File.WriteAllText(Path.Combine(projDir, "CrossProjectServiceImpl.cs"), @"using Core;

namespace Services;

public class CrossProjectServiceImpl : ICrossProjectService
{
    public int ProcessData(string input, int count)
    {
        return input.Length + count;
    }

    public bool ValidateInput(string input)
    {
        return !string.IsNullOrEmpty(input);
    }

    public string TransformResult(int value)
    {
        return value.ToString();
    }

    public int AdditionalMethod(int x, int y)
    {
        return x * y + ProcessData(x.ToString(), y);
    }
}");
        }
    }

    private static string GenerateParameters(Random rng, int minCount, int maxCount)
    {
        int count = minCount + rng.Next(maxCount - minCount + 1);
        var parms = new List<string>();
        for (int i = 0; i < count; i++)
        {
            var pType = rng.Next(4) switch { 0 => "int", 1 => "string", 2 => "bool", _ => "double" };
            parms.Add($"{pType} p{i}");
        }
        return string.Join(", ", parms);
    }

    // ── Edit application (apply + revert for each type) ──────
    // Each edit type saves original state before modifying, so it can be cleanly reverted.

    private Dictionary<string, string> _editBackups = new();

    private void Backup(string relativeFilePath)
    {
        var path = Path.Combine(_testDir, relativeFilePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path))
            _editBackups[relativeFilePath] = File.ReadAllText(path);
    }

    private void Restore(string relativeFilePath)
    {
        var path = Path.Combine(_testDir, relativeFilePath.Replace('/', Path.DirectorySeparatorChar));
        if (_editBackups.TryGetValue(relativeFilePath, out var original))
        {
            File.WriteAllText(path, original);
            _editBackups.Remove(relativeFilePath);
        }
        else if (File.Exists(path) && !_editBackups.ContainsKey(relativeFilePath))
        {
            // File was created by the edit (e.g., NewType.cs); delete it
            File.Delete(path);
        }
    }

    private void ApplyMethodBodyEdit()
    {
        Backup("src/Core/CoreClass0.cs");
        var path = Path.Combine(_testDir, "src", "Core", "CoreClass0.cs");
        if (!File.Exists(path)) return;
        var content = File.ReadAllText(path);
        content = content.Replace("return default;", "System.Console.WriteLine(\"CoreClass0 invoked\"); return default;");
        File.WriteAllText(path, content);
    }
    private void RevertMethodBodyEdit() { Restore("src/Core/CoreClass0.cs"); }

    private void ApplySignatureChange()
    {
        Backup("src/Core/CoreClass1.cs");
        var path = Path.Combine(_testDir, "src", "Core", "CoreClass1.cs");
        if (!File.Exists(path)) return;
        var content = File.ReadAllText(path);
        content = content.Replace("DoCoreWork0()", "DoCoreWork0(int newParam = 0)");
        File.WriteAllText(path, content);
    }
    private void RevertSignatureChange() { Restore("src/Core/CoreClass1.cs"); }

    private void ApplyNewType()
    {
        var path = Path.Combine(_testDir, "src", "Core", "NewType.cs");
        File.WriteAllText(path, @"namespace Core;

public class NewType
{
    public string Name { get; set; } = string.Empty;
    public int Value { get; set; }

    public int Calculate()
    {
        return Value * 2;
    }

    public override string ToString()
    {
        return $""{Name}: {Value}"";
    }
}");
    }
    private void RevertNewType() { Restore("src/Core/NewType.cs"); }

    private void ApplyDeleteType()
    {
        Backup("src/Core/CoreClass19.cs");
        var path = Path.Combine(_testDir, "src", "Core", "CoreClass19.cs");
        if (File.Exists(path))
            File.Delete(path);
    }
    private void RevertDeleteType() { Restore("src/Core/CoreClass19.cs"); }

    private void ApplyInterfaceChange()
    {
        Backup("src/Core/ICrossProjectService.cs");
        Backup("src/Services/CrossProjectServiceImpl.cs");

        var ifacePath = Path.Combine(_testDir, "src", "Core", "ICrossProjectService.cs");
        if (File.Exists(ifacePath))
        {
            var content = File.ReadAllText(ifacePath);
            content = content.Replace("int ProcessData(string input, int count);",
                "Task<int> ProcessDataAsync(string input, int count, CancellationToken cancellationToken = default);");
            File.WriteAllText(ifacePath, content);
        }

        var implPath = Path.Combine(_testDir, "src", "Services", "CrossProjectServiceImpl.cs");
        if (File.Exists(implPath))
        {
            var implContent = File.ReadAllText(implPath);
            implContent = implContent.Replace(
                "public int ProcessData(string input, int count)",
                "public async Task<int> ProcessDataAsync(string input, int count, CancellationToken cancellationToken = default)");
            implContent = implContent.Replace(
                "return input.Length + count;",
                "await Task.Delay(0, cancellationToken); return input.Length + count;");
            implContent = implContent.Replace(
                "return x * y + ProcessData(x.ToString(), y);",
                "return x * y + ProcessDataAsync(x.ToString(), y).Result;");
            File.WriteAllText(implPath, implContent);
        }
    }
    private void RevertInterfaceChange()
    {
        Restore("src/Services/CrossProjectServiceImpl.cs");
        Restore("src/Core/ICrossProjectService.cs");
    }

    private void ResetToBaselineState()
    {
        SqliteConnectionClearAllPools();

        // Delete all generated files and recreate from scratch
        if (Directory.Exists(Path.Combine(_testDir, "src")))
            Directory.Delete(Path.Combine(_testDir, "src"), recursive: true);

        // Recreate the solution
        CreateRealisticSolution();
    }

    // ── Indexing helpers ────────────────────────────────────

    private async Task<string> RunFullIndexAsync(string label)
    {
        _output.WriteLine($"--- {label} ---");

        if (File.Exists(_dbPath))
            File.Delete(_dbPath);

        var store = new SqliteIndexStore(_dbPath);
        store.Open(_dbPath);
        store.RunMigrations();

        try
        {
            using var workspace = MSBuildWorkspace.Create();
            workspace.RegisterWorkspaceFailedHandler(args =>
            {
                _output.WriteLine($"  [Workspace] {args.Diagnostic.Kind}: {args.Diagnostic.Message}");
            });

            var solution = await workspace.OpenSolutionAsync(_solutionPath);
            var gitRoot = _testDir;
            var workspaceInfo = new WorkspaceInfo(solution, gitRoot);

            var snapshotId = SnapshotId.New();
            var manifest = SnapshotManifest.FromWorkspace(workspaceInfo, snapshotId);
            var snapshotIdStr = snapshotId.ToString();
            var timings = new List<SnapshotTimingRow>();

            var swManifest = System.Diagnostics.Stopwatch.StartNew();
            manifest.Save(store, store, workspaceInfo.DocumentContents, jsonExportPath: null);
            swManifest.Stop();
            timings.Add(new SnapshotTimingRow("manifest_save", swManifest.ElapsedMilliseconds, DateTime.UtcNow));

            store.MarkSnapshotInProgress(snapshotIdStr);

            try
            {
                int totalDecl = 0, totalEdge = 0, totalDiag = 0;

                var swExtract = System.Diagnostics.Stopwatch.StartNew();
                foreach (var (project, compilation) in await GetAllAsync(solution))
                {
                    var projectName = project.Name;
                    _output.WriteLine($"    [{projectName}]");

                    var result = CompilationFactExtractor.ExtractAll(
                        compilation, workspaceInfo, snapshotIdStr, projectName,
                        skipAdapters: new HashSet<string>());

                    store.SaveDeclarations(snapshotIdStr, result.Declarations);
                    totalDecl += result.Declarations.Count;
                    store.SaveEdges(snapshotIdStr, result.Edges);
                    totalEdge += result.Edges.Count;
                    store.SaveDiagnostics(snapshotIdStr, result.Diagnostics);
                    totalDiag += result.Diagnostics.Count;

                    _output.WriteLine($"      {result.Declarations.Count} symbols, {result.Edges.Count} edges, {result.Diagnostics.Count} diagnostics.");
                }
                swExtract.Stop();
                timings.Add(new SnapshotTimingRow("extraction_loop", swExtract.ElapsedMilliseconds, DateTime.UtcNow));

                _output.WriteLine($"    Total: {totalDecl} symbols, {totalEdge} edges, {totalDiag} diagnostics.");

                var previousManifest = store.LoadLatestSnapshot(manifest.WorkspaceId.Value);
                if (previousManifest != null && previousManifest.SnapshotId != snapshotIdStr)
                {
                    var swDiff = System.Diagnostics.Stopwatch.StartNew();
                    var differ = new SemanticDiffer(store, store, store);
                    var diffChanges = differ.ComputeDiff(previousManifest.SnapshotId, snapshotIdStr);
                    store.SaveSemanticChanges(previousManifest.SnapshotId, snapshotIdStr, diffChanges);
                    swDiff.Stop();
                    timings.Add(new SnapshotTimingRow("semantic_diff", swDiff.ElapsedMilliseconds, DateTime.UtcNow));
                }

                store.MarkSnapshotComplete(snapshotIdStr);
                try { store.SaveTimings(snapshotIdStr, timings); }
                catch (Exception ex) { _output.WriteLine($"  WARNING: Failed to save timings: {ex.Message}"); }

                return snapshotIdStr;
            }
            catch
            {
                try { store.SaveTimings(snapshotIdStr, timings); } catch { }
                throw;
            }
        }
        finally
        {
            // Skip prune during measurement to avoid FK issues with accumulated snapshots
            store.Close();
        }
    }

    private async Task<string> RunIncrementalIndexAsync(string label)
    {
        _output.WriteLine($"--- {label} ---");

        var store = new SqliteIndexStore(_dbPath);
        store.Open(_dbPath);
        store.RunMigrations();

        try
        {
            using var workspace = MSBuildWorkspace.Create();
            workspace.RegisterWorkspaceFailedHandler(args =>
            {
                _output.WriteLine($"  [Workspace] {args.Diagnostic.Kind}: {args.Diagnostic.Message}");
            });

            var solution = await workspace.OpenSolutionAsync(_solutionPath);
            var gitRoot = _testDir;
            var workspaceInfo = new WorkspaceInfo(solution, gitRoot);

            var previousManifest = store.LoadLatestSnapshot(workspaceInfo.Id.Value);
            if (previousManifest == null)
                throw new InvalidOperationException("No previous snapshot found.");

            var incrementalIndexer = new IncrementalIndexer(
                store, gitRoot, _solutionPath, _testDir,
                skipAdapters: [],
                jsonExportPath: null);

            var result = await incrementalIndexer.RunIncrementalAsync(
                solution, workspaceInfo, previousManifest);

            _output.WriteLine($"    New snapshot: {result.NewSnapshotId}");
            _output.WriteLine($"    Changed docs: {result.ChangedDocumentCount}");
            _output.WriteLine($"    Declarations: {result.DeclarationsExtracted}");
            _output.WriteLine($"    Edges: {result.EdgesExtracted}");

            return result.NewSnapshotId;
        }
        finally
        {
            // Skip prune during measurement
            store.Close();
        }
    }

    private List<SnapshotTimingRow> ReadTimings(string snapshotId)
    {
        var store = new SqliteIndexStore(_dbPath);
        store.Open(_dbPath);
        try
        {
            return store.GetTimings(snapshotId);
        }
        finally
        {
            store.Close();
        }
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
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
        catch { }
    }
}
