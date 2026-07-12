using System.Collections.Immutable;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynIndexer;

/// <summary>
/// Describes the current live workspace state at the time of construction.
/// Every persisted item can be traced to exactly one version of one workspace
/// under one compilation configuration through these properties.
/// </summary>
public sealed class WorkspaceInfo
{
    /// <summary>Composite identity of the workspace (Git root + solution path).</summary>
    public WorkspaceId Id { get; }

    /// <summary>
    /// Every source document in the solution, mapped to its SHA-256 content version.
    /// Keyed by stable logical path (relative to Git root, forward slashes).
    /// </summary>
    public IReadOnlyDictionary<DocumentId, DocumentVersionId> Documents { get; }

    /// <summary>.NET SDK version string (e.g. "10.0.301") from the registered MSBuild instance.</summary>
    public string SdkVersion { get; }

    /// <summary>Roslyn compiler assembly version (e.g. 4.12.0.0).</summary>
    public Version CompilerVersion { get; }

    /// <summary>
    /// Target framework moniker per project (e.g. "net10.0").
    /// Extracted from each project's .csproj file.
    /// </summary>
    public IReadOnlyDictionary<string, string> TargetFrameworks { get; }

    /// <summary>
    /// Project dependency graph: project name → set of direct project reference names.
    /// </summary>
    public IReadOnlyDictionary<string, ImmutableHashSet<string>> ProjectGraph { get; }

    /// <summary>Indexer tool version (from <see cref="VersionConstants.ToolVersion"/>).</summary>
    public string IndexerVersion { get; }

    /// <summary>Extractor version (from <see cref="VersionConstants.ExtractorVersion"/>).</summary>
    public string ExtractorVersion { get; }

    /// <summary>
    /// Constructs a <see cref="WorkspaceInfo"/> from a loaded Roslyn <see cref="Solution"/>
    /// and the absolute Git root path.
    /// </summary>
    /// <param name="solution">A fully loaded MSBuildWorkspace solution.</param>
    /// <param name="gitRoot">The absolute path to the Git repository root.</param>
    public WorkspaceInfo(Solution solution, string gitRoot)
    {
        Id = WorkspaceId.Create(gitRoot, solution.FilePath ?? "");

        Documents = BuildDocumentMap(solution, gitRoot);

        SdkVersion = QuerySdkVersion();

        CompilerVersion = typeof(CSharpCompilation).Assembly.GetName().Version
                          ?? new Version(0, 0);

        TargetFrameworks = BuildTargetFrameworkMap(solution);

        ProjectGraph = BuildProjectGraph(solution);

        IndexerVersion = VersionConstants.ToolVersion;
        ExtractorVersion = VersionConstants.ExtractorVersion;
    }

    // ----------------------------------------------------------------
    // Private helpers
    // ----------------------------------------------------------------

    private static Dictionary<DocumentId, DocumentVersionId> BuildDocumentMap(
        Solution solution, string gitRoot)
    {
        var map = new Dictionary<DocumentId, DocumentVersionId>();
        var normalizedRoot = Path.GetFullPath(gitRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath == null) continue; // skip generated

                var relPath = GetRelativePath(document.FilePath, normalizedRoot);
                var docId = new DocumentId(relPath);

                // Read from disk — solution was freshly loaded, so disk matches.
                var hash = DocumentVersionId.Compute(File.ReadAllBytes(document.FilePath));

                map[docId] = hash;
            }
        }

        return map;
    }

    private static string GetRelativePath(string fullPath, string normalizedRoot)
    {
        var root = normalizedRoot + Path.DirectorySeparatorChar;
        return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
    }

    private static string QuerySdkVersion()
    {
        try
        {
            var instances = MSBuildLocator.QueryVisualStudioInstances(
                new VisualStudioInstanceQueryOptions
                {
                    DiscoveryTypes = DiscoveryType.DotNetSdk
                });

            return instances
                .OrderByDescending(i => i.Version)
                .FirstOrDefault()?.Version.ToString()
                ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static Dictionary<string, string> BuildTargetFrameworkMap(Solution solution)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            if (project.FilePath == null || !File.Exists(project.FilePath))
            {
                map[project.Name] = "unknown";
                continue;
            }

            try
            {
                var doc = XDocument.Load(project.FilePath);
                var root = doc.Root;
                if (root == null) { map[project.Name] = "unknown"; continue; }

                // SDK-style projects use the default namespace.
                XNamespace ns = root.GetDefaultNamespace();

                // First <PropertyGroup> usually contains TargetFramework.
                var tf = root
                    .Elements(ns + "PropertyGroup")
                    .SelectMany(pg => pg.Elements(ns + "TargetFramework"))
                    .Select(e => e.Value.Trim())
                    .FirstOrDefault();

                tf ??= root
                    .Elements(ns + "PropertyGroup")
                    .SelectMany(pg => pg.Elements(ns + "TargetFrameworks"))
                    .Select(e => e.Value.Trim())
                    .FirstOrDefault();

                map[project.Name] = tf ?? "unknown";
            }
            catch
            {
                map[project.Name] = "unknown";
            }
        }

        return new Dictionary<string, string>(map, StringComparer.Ordinal);
    }

    private static Dictionary<string, ImmutableHashSet<string>> BuildProjectGraph(
        Solution solution)
    {
        var projectIdToName = solution.Projects.ToDictionary(p => p.Id, p => p.Name);

        var graph = new Dictionary<string, ImmutableHashSet<string>>(
            StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            var refs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pr in project.ProjectReferences)
            {
                if (projectIdToName.TryGetValue(pr.ProjectId, out var name))
                    refs.Add(name);
            }

            graph[project.Name] = refs.ToImmutableHashSet(StringComparer.Ordinal);
        }

        return graph;
    }
}
