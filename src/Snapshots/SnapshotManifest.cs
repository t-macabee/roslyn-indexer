using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynIndexer;

/// <summary>
/// Persisted representation of one workspace snapshot.
/// Every indexed item can be traced to exactly one version of one workspace
/// under one compilation configuration through this manifest.
/// </summary>
public sealed class SnapshotManifest
{
    // ── Identity ────────────────────────────────────────────────────

    [JsonPropertyName("snapshotId")]
    [JsonConverter(typeof(SnapshotIdConverter))]
    public SnapshotId SnapshotId { get; init; }

    [JsonPropertyName("workspaceId")]
    [JsonConverter(typeof(WorkspaceIdConverter))]
    public WorkspaceId WorkspaceId { get; init; }

    [JsonPropertyName("builtAtUtc")]
    public DateTime BuiltAtUtc { get; init; }

    // ── Documents ───────────────────────────────────────────────────

    /// <summary>Document → content-version mapping at snapshot time.</summary>
    [JsonPropertyName("documentVersions")]
    [JsonConverter(typeof(DocumentVersionMapConverter))]
    public Dictionary<DocumentId, DocumentVersionId> DocumentVersions { get; init; }
        = new Dictionary<DocumentId, DocumentVersionId>();

    // ── Environment ─────────────────────────────────────────────────

    [JsonPropertyName("sdkVersion")]
    public string SdkVersion { get; init; } = "";

    /// <summary>Roslyn compiler version as a string (e.g. "4.12.0.0").</summary>
    [JsonPropertyName("compilerVersion")]
    public string CompilerVersion { get; init; } = "";

    /// <summary>Target framework per project (project name → TFM).</summary>
    [JsonPropertyName("targetFrameworks")]
    public Dictionary<string, string> TargetFrameworks { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Project dependency graph (project name → direct reference names).</summary>
    [JsonPropertyName("projectGraph")]
    public Dictionary<string, string[]> ProjectGraph { get; init; }
        = new Dictionary<string, string[]>();

    // ── Version pins ────────────────────────────────────────────────

    [JsonPropertyName("databaseSchemaVersion")]
    public int DatabaseSchemaVersion { get; init; }

    [JsonPropertyName("outputSchemaVersion")]
    public int OutputSchemaVersion { get; init; }

    [JsonPropertyName("extractorVersion")]
    public string ExtractorVersion { get; init; } = "";

    [JsonPropertyName("toolVersion")]
    public string ToolVersion { get; init; } = "";

    // ── Diff chain ──────────────────────────────────────────────────

    /// <summary>Link to the previous snapshot to enable diff chains.</summary>
    [JsonPropertyName("previousSnapshotId")]
    [JsonConverter(typeof(NullableSnapshotIdConverter))]
    public SnapshotId? PreviousSnapshotId { get; init; }

    // ── Factory ────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="SnapshotManifest"/> from live workspace info and a snapshot ID.
    /// </summary>
    public static SnapshotManifest FromWorkspace(
        WorkspaceInfo workspace,
        SnapshotId snapshotId,
        SnapshotId? previousSnapshotId = null)
    {
        return new SnapshotManifest
        {
            SnapshotId = snapshotId,
            WorkspaceId = workspace.Id,
            BuiltAtUtc = DateTime.UtcNow,
            DocumentVersions = new Dictionary<DocumentId, DocumentVersionId>(workspace.Documents),
            SdkVersion = workspace.SdkVersion,
            CompilerVersion = workspace.CompilerVersion.ToString(),
            TargetFrameworks = new Dictionary<string, string>(workspace.TargetFrameworks),
            ProjectGraph = workspace.ProjectGraph.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.OrderBy(x => x).ToArray(),
                StringComparer.Ordinal),
            DatabaseSchemaVersion = VersionConstants.DatabaseSchemaVersion,
            OutputSchemaVersion = VersionConstants.OutputSchemaVersion,
            ExtractorVersion = VersionConstants.ExtractorVersion,
            ToolVersion = VersionConstants.ToolVersion,
            PreviousSnapshotId = previousSnapshotId,
        };
    }

    // ── Serialisation helpers ───────────────────────────────────────

    /// <summary>Default JSON options for reading/writing manifests.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Writes the manifest to the specified file path.</summary>
    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>Reads a manifest from the specified file path.</summary>
    public static SnapshotManifest Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SnapshotManifest>(json, JsonOptions)
               ?? throw new InvalidOperationException("Failed to deserialize snapshot manifest.");
    }

    // ════════════════════════════════════════════════════════════════
    //  JSON converters for the identity value types
    // ════════════════════════════════════════════════════════════════

    private sealed class SnapshotIdConverter : JsonConverter<SnapshotId>
    {
        public override SnapshotId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => SnapshotId.Parse(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, SnapshotId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }

    private sealed class NullableSnapshotIdConverter : JsonConverter<SnapshotId?>
    {
        public override SnapshotId? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            return SnapshotId.Parse(reader.GetString()!);
        }

        public override void Write(Utf8JsonWriter writer, SnapshotId? value, JsonSerializerOptions options)
        {
            if (value is null) writer.WriteNullValue();
            else writer.WriteStringValue(value.Value.ToString());
        }
    }

    private sealed class WorkspaceIdConverter : JsonConverter<WorkspaceId>
    {
        public override WorkspaceId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected JSON object for WorkspaceId.");

            string? gitRoot = null, solutionPath = null, value = null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var prop = reader.GetString();
                reader.Read();
                switch (prop)
                {
                    case "gitRoot": gitRoot = reader.GetString(); break;
                    case "solutionPath": solutionPath = reader.GetString(); break;
                    case "value": value = reader.GetString(); break;
                    default: reader.Skip(); break;
                }
            }

            if (gitRoot != null && solutionPath != null)
                return WorkspaceId.Create(gitRoot, solutionPath);

            if (value != null)
                return ParseWorkspaceUri(value);

            throw new JsonException("Insufficient data to reconstruct WorkspaceId.");
        }

        public override void Write(Utf8JsonWriter writer, WorkspaceId value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("gitRoot", value.GitRoot);
            writer.WriteString("solutionPath", value.SolutionPath);
            writer.WriteString("value", value.Value);
            writer.WriteEndObject();
        }

        private static WorkspaceId ParseWorkspaceUri(string uri)
        {
            // Format: workspace://{gitRoot}/{relativeOrAbsoluteSolutionPath}
            const string prefix = "workspace://";
            if (!uri.StartsWith(prefix, StringComparison.Ordinal))
                throw new JsonException($"Invalid WorkspaceId URI: {uri}");

            var rest = uri[prefix.Length..];
            var slashIndex = rest.IndexOf('/');
            if (slashIndex < 0)
                throw new JsonException($"Invalid WorkspaceId URI (no root/solution split): {uri}");

            var gitRoot = rest[..slashIndex];
            var slnPath = rest[(slashIndex + 1)..];
            var fullSlnPath = Path.GetFullPath(Path.Combine(gitRoot, slnPath));
            return WorkspaceId.Create(gitRoot, fullSlnPath);
        }
    }

    private sealed class DocumentVersionMapConverter
        : JsonConverter<Dictionary<DocumentId, DocumentVersionId>>
    {
        public override Dictionary<DocumentId, DocumentVersionId> Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var result = new Dictionary<DocumentId, DocumentVersionId>();

            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected JSON object for documentVersions.");

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var docPath = reader.GetString()!;
                reader.Read();
                var hash = reader.GetString()!;
                result[new DocumentId(docPath)] = new DocumentVersionId(hash);
            }

            return result;
        }

        public override void Write(
            Utf8JsonWriter writer, Dictionary<DocumentId, DocumentVersionId> value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var kvp in value)
            {
                writer.WriteString(kvp.Key.ToString(), kvp.Value.ToString());
            }
            writer.WriteEndObject();
        }
    }
}
