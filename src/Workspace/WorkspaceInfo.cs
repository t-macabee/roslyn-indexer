using System.Collections.Immutable;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynIndexer;






public sealed class WorkspaceInfo
{
    
    public WorkspaceId Id { get; }

    
    
    
    
    public IReadOnlyDictionary<DocumentId, DocumentVersionId> Documents { get; }

    
    public string SdkVersion { get; }

    
    public Version CompilerVersion { get; }

    
    
    
    
    public IReadOnlyDictionary<string, string> TargetFrameworks { get; }

    
    
    
    public IReadOnlyDictionary<string, ImmutableHashSet<string>> ProjectGraph { get; }

    
    public string IndexerVersion { get; }

    
    public string ExtractorVersion { get; }

    
    
    
    
    
    
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
                if (document.FilePath == null) continue; 

                var relPath = GetRelativePath(document.FilePath, normalizedRoot);
                var docId = new DocumentId(relPath);

                
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

                
                XNamespace ns = root.GetDefaultNamespace();

                
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
