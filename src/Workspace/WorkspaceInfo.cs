using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lurp;

public sealed class WorkspaceInfo
{
    public WorkspaceId Id { get; }

    public IReadOnlyDictionary<DocumentId, DocumentVersionId> Documents { get; }

    public IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)> DocumentContents { get; }

    public string SdkVersion { get; }

    public Version CompilerVersion { get; }

    public IReadOnlyDictionary<string, string> TargetFrameworks { get; }

    public IReadOnlyDictionary<string, ImmutableHashSet<string>> ProjectGraph { get; }

    public string IndexerVersion { get; }

    public string ExtractorVersion { get; }

    public WorkspaceInfo(Solution solution, string gitRoot)
    {
        Id = WorkspaceId.Create(gitRoot, solution.FilePath ?? "");

        var (documents, contents) = BuildDocumentMap(solution, gitRoot);
        Documents = documents;
        DocumentContents = contents;

        SdkVersion = QuerySdkVersion();

        CompilerVersion = typeof(CSharpCompilation).Assembly.GetName().Version
                          ?? new Version(0, 0);

        TargetFrameworks = BuildTargetFrameworkMap(solution);

        ProjectGraph = BuildProjectGraph(solution);

        IndexerVersion = VersionConstants.ToolVersion;
        ExtractorVersion = VersionConstants.ExtractorVersion;
    }

    private static (Dictionary<DocumentId, DocumentVersionId> Hashes,
                    Dictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)> Contents)
        BuildDocumentMap(Solution solution, string gitRoot)
    {
        var map = new Dictionary<DocumentId, DocumentVersionId>();
        var contentMap = new Dictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)>();
        var normalizedRoot = Path.GetFullPath(gitRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath == null) continue;

                var relPath = GetRelativePath(document.FilePath, normalizedRoot);
                var docId = new DocumentId(relPath);

                var bytes = File.ReadAllBytes(document.FilePath);
                var hash = DocumentVersionId.Compute(bytes);
                var encoding = DetectEncoding(bytes);
                var lineStarts = ComputeLineStarts(bytes);

                map[docId] = hash;
                contentMap[docId] = (bytes, encoding, lineStarts);
            }
        }

        return (map, contentMap);
    }

    private static string DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return "utf-8-bom";
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return "utf-16-le";
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return "utf-16-be";
        return "utf-8";
    }

    private static string ComputeLineStarts(byte[] bytes)
    {
        var offsets = new List<int> { 0 };
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                if (i + 1 < bytes.Length)
                    offsets.Add(i + 1);
            }
        }
        return JsonSerializer.Serialize(offsets);
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

