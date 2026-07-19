using System.Text.Json.Serialization;

namespace Lurp.Storage
{
    public class SnapshotManifest
    {
        public string SnapshotId { get; }
        public string WorkspaceId { get; }
        public string GitRoot { get; }
        public string SolutionPath { get; }
        public string SdkVersion { get; }
        public string CompilerVersion { get; }
        public DateTime CreatedAtUtc { get; }
        public List<DocumentVersion> Documents { get; }

        public SnapshotManifest(string snapshotId,string workspaceId,string gitRoot,string solutionPath,string sdkVersion,string compilerVersion,DateTime createdAtUtc,List<DocumentVersion>? documents = null)
        {
            SnapshotId = snapshotId;
            WorkspaceId = workspaceId;
            GitRoot = gitRoot;
            SolutionPath = solutionPath;
            SdkVersion = sdkVersion;
            CompilerVersion = compilerVersion;
            CreatedAtUtc = createdAtUtc;
            Documents = documents ?? new List<DocumentVersion>();
        }
    }

    public sealed class EdgeRecord
    {
        public string SourceSymbolId { get; }
        public string TargetSymbolId { get; }
        public string Kind { get; }
        public string Provenance { get; }
        public string SnapshotId { get; }
        public string ExtractorVersion { get; }
        public string? SourceDocumentPath { get; }
        public int? SourceStartLine { get; }
        public int? SourceStartColumn { get; }
        public int? SourceEndLine { get; }
        public int? SourceEndColumn { get; }

        public EdgeRecord(string sourceSymbolId,string targetSymbolId,string kind,string provenance,string snapshotId,string extractorVersion,string? sourceDocumentPath = null,int? sourceStartLine = null,int? sourceStartColumn = null,int? sourceEndLine = null,int? sourceEndColumn = null)
        {
            SourceSymbolId = sourceSymbolId ?? throw new ArgumentNullException(nameof(sourceSymbolId));
            TargetSymbolId = targetSymbolId ?? throw new ArgumentNullException(nameof(targetSymbolId));
            Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
            SnapshotId = snapshotId ?? throw new ArgumentNullException(nameof(snapshotId));
            ExtractorVersion = extractorVersion ?? throw new ArgumentNullException(nameof(extractorVersion));
            SourceDocumentPath = sourceDocumentPath;
            SourceStartLine = sourceStartLine;
            SourceStartColumn = sourceStartColumn;
            SourceEndLine = sourceEndLine;
            SourceEndColumn = sourceEndColumn;
        }

        public EdgeRecord(string sourceSymbolId,string targetSymbolId,string kind,string? provenance = null)
            : this(sourceSymbolId,targetSymbolId,kind,provenance ?? string.Empty,snapshotId: string.Empty,extractorVersion: string.Empty)
        {
        }
    }

    public sealed class DiagnosticRecord
    {
        public string ProjectName { get; }
        public string? DocumentPath { get; }
        public string Severity { get; }
        public string Id { get; }
        public string Message { get; }
        public int? StartLine { get; }
        public int? StartColumn { get; }
        public int? EndLine { get; }
        public int? EndColumn { get; }

        public DiagnosticRecord(string projectName,string? documentPath,string severity,string id,string message,int? startLine = null,int? startColumn = null,int? endLine = null,int? endColumn = null)
        {
            ProjectName = projectName ?? throw new ArgumentNullException(nameof(projectName));
            DocumentPath = documentPath;
            Severity = severity ?? throw new ArgumentNullException(nameof(severity));
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Message = message ?? throw new ArgumentNullException(nameof(message));
            StartLine = startLine;
            StartColumn = startColumn;
            EndLine = endLine;
            EndColumn = endColumn;
        }
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
        public string DocumentId { get; }
        public string FilePath { get; }
        public string ContentHash { get; }
        public string Encoding { get; }
        public string LineStart { get; }
        public DateTime CreatedAtUtc { get; }

        public byte[]? Content { get; }
        public int ByteCount => Content?.Length ?? 0;
        public string? LineStarts { get; }

        public DocumentVersion(string documentId,string filePath,string contentHash,string encoding,string lineStart,DateTime createdAtUtc)
        {
            DocumentId = documentId;
            FilePath = filePath;
            ContentHash = contentHash;
            Encoding = encoding;
            LineStart = lineStart;
            CreatedAtUtc = createdAtUtc;
        }

        public DocumentVersion(string documentId,string filePath,string contentHash,string encoding,string lineStart,DateTime createdAtUtc,byte[] content,string lineStarts)
            : this(documentId, filePath, contentHash, encoding, lineStart, createdAtUtc)
        {
            Content = content;
            LineStarts = lineStarts;
        }
    }
}
