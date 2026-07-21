namespace Lurp.Storage
{
    public class SnapshotRow
    {
        public string SnapshotId { get; init; } = string.Empty;
        public string WorkspaceId { get; init; } = string.Empty;
        public string GitRoot { get; init; } = string.Empty;
        public string SolutionPath { get; init; } = string.Empty;
        public string SdkVersion { get; init; } = string.Empty;
        public string CompilerVersion { get; init; } = string.Empty;
        public DateTime CreatedAtUtc { get; init; }
        public List<DocumentVersion> Documents { get; init; } = [];
        public int DatabaseSchemaVersion { get; init; }
        public int OutputSchemaVersion { get; init; }
        public string ExtractorVersion { get; init; } = string.Empty;
        public string ToolVersion { get; init; } = string.Empty;
        public string? PreviousSnapshotId { get; init; }
        public List<ProjectRow> Projects { get; init; } = [];
    }

    public sealed class ProjectRow
    {
        public string Name { get; init; } = string.Empty;
        public string TargetFramework { get; init; } = string.Empty;
        public List<string> References { get; init; } = [];
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
        public const string ComparisonUnavailable = "comparison_unavailable";
    }

    public sealed class SemanticChange
    {
        public string ChangeId { get; init; } = string.Empty;
        public string FromSnapshotId { get; init; } = string.Empty;
        public string ToSnapshotId { get; init; } = string.Empty;
        public string ChangeType { get; init; } = string.Empty;
        public string SymbolId { get; init; } = string.Empty;
        public string? DetailJson { get; init; }
        public DateTime CreatedAtUtc { get; init; }
    }

    public sealed class SnapshotTimingRow
    {
        public string StepName { get; }
        public long ElapsedMs { get; }
        public DateTime CreatedAtUtc { get; }

        public SnapshotTimingRow(string stepName, long elapsedMs, DateTime createdAtUtc)
        {
            StepName = stepName ?? throw new ArgumentNullException(nameof(stepName));
            ElapsedMs = elapsedMs;
            CreatedAtUtc = createdAtUtc;
        }
    }

    public class DocumentVersion
    {
        public DocumentVersion() { }

        public DocumentVersion(byte[]? content) => Content = content;

        public string DocumentId { get; init; } = string.Empty;
        public string FilePath { get; init; } = string.Empty;
        public string ContentHash { get; init; } = string.Empty;
        public string Encoding { get; init; } = string.Empty;
        public string LineStart { get; init; } = string.Empty;
        public DateTime CreatedAtUtc { get; init; }

        public byte[]? Content { get; }
        public int ByteCount => Content?.Length ?? 0;
        public string? LineStarts { get; init; }
    }
}
