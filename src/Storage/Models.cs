using System.Text.Json.Serialization;

namespace Lurp.Storage
{
    public class SnapshotManifest
    {
        public string SnapshotId { get; init; } = string.Empty;
        public string WorkspaceId { get; init; } = string.Empty;
        public string GitRoot { get; init; } = string.Empty;
        public string SolutionPath { get; init; } = string.Empty;
        public string SdkVersion { get; init; } = string.Empty;
        public string CompilerVersion { get; init; } = string.Empty;
        public DateTime CreatedAtUtc { get; init; }
        public List<DocumentVersion> Documents { get; init; } = new();
    }

    public sealed class EdgeRecord
    {
        public string SourceSymbolId { get; init; } = string.Empty;
        public string TargetSymbolId { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string Provenance { get; init; } = string.Empty;
        public string SnapshotId { get; init; } = string.Empty;
        public string ExtractorVersion { get; init; } = string.Empty;
        public string? SourceDocumentPath { get; init; }
        public int? SourceStartLine { get; init; }
        public int? SourceStartColumn { get; init; }
        public int? SourceEndLine { get; init; }
        public int? SourceEndColumn { get; init; }
    }

    public sealed class DiagnosticRecord
    {
        public string ProjectName { get; init; } = string.Empty;
        public string? DocumentPath { get; init; }
        public string Severity { get; init; } = string.Empty;
        public string Id { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public int? StartLine { get; init; }
        public int? StartColumn { get; init; }
        public int? EndLine { get; init; }
        public int? EndColumn { get; init; }
    }

    public sealed class AnnotationRecord
    {
        public string SymbolId { get; }
        public string Kind { get; }
        public string Value { get; }

        public AnnotationRecord(string symbolId, string kind, string value)
        {
            SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
            Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            Value = value ?? throw new ArgumentNullException(nameof(value));
        }
    }

    public static class ChangeType
    {
        public const string SymbolAdded = "symbol_added";
        public const string SymbolRemoved = "symbol_removed";
        public const string SymbolRenamed = "symbol_renamed";
        public const string SymbolMoved = "symbol_moved";
        public const string AccessibilityChanged = "accessibility_changed";
        public const string SignatureChanged = "signature_changed";
        public const string BaseTypeChanged = "base_type_changed";
        public const string EdgeAdded = "edge_added";
        public const string EdgeRemoved = "edge_removed";
        public const string AttributeChanged = "attribute_changed";
        public const string BodyOnlyChanged = "body_only_changed";
    }

    public sealed class SemanticChange
    {
        [JsonConstructor]
        public SemanticChange(string changeId,string fromSnapshotId,string toSnapshotId,string changeType,string symbolId,string? detailJson,DateTime createdAtUtc)
        {
            ChangeId = changeId ?? throw new ArgumentNullException(nameof(changeId));
            FromSnapshotId = fromSnapshotId ?? throw new ArgumentNullException(nameof(fromSnapshotId));
            ToSnapshotId = toSnapshotId ?? throw new ArgumentNullException(nameof(toSnapshotId));
            ChangeType = changeType ?? throw new ArgumentNullException(nameof(changeType));
            SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
            DetailJson = detailJson;
            CreatedAtUtc = createdAtUtc;
        }

        public string ChangeId { get; }
        public string FromSnapshotId { get; }
        public string ToSnapshotId { get; }
        public string ChangeType { get; }
        public string SymbolId { get; }
        public string? DetailJson { get; }
        public DateTime CreatedAtUtc { get; }
    }

    public class DocumentVersion
    {
        public string DocumentId { get; init; } = string.Empty;
        public string FilePath { get; init; } = string.Empty;
        public string ContentHash { get; init; } = string.Empty;
        public string Encoding { get; init; } = string.Empty;
        public string LineStart { get; init; } = string.Empty;
        public DateTime CreatedAtUtc { get; init; }

        public byte[]? Content { get; init; }
        public int ByteCount => Content?.Length ?? 0;
        public string? LineStarts { get; init; }
    }
}
