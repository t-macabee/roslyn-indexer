using System;
using System.Collections.Generic;

namespace RoslynIndexer.Storage
{
    public class WorkspaceId
    {
        public string Value { get; }

        public WorkspaceId(string value)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
        }

        public override bool Equals(object? obj)
        {
            return obj is WorkspaceId other && Value == other.Value;
        }

        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }
    }

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

        public SnapshotManifest(
            string snapshotId,
            string workspaceId,
            string gitRoot,
            string solutionPath,
            string sdkVersion,
            string compilerVersion,
            DateTime createdAtUtc,
            List<DocumentVersion>? documents = null)
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
        public string? Provenance { get; }

        public EdgeRecord(string sourceSymbolId, string targetSymbolId, string kind, string? provenance = null)
        {
            SourceSymbolId = sourceSymbolId ?? throw new ArgumentNullException(nameof(sourceSymbolId));
            TargetSymbolId = targetSymbolId ?? throw new ArgumentNullException(nameof(targetSymbolId));
            Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            Provenance = provenance;
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

        public DiagnosticRecord(
            string projectName,
            string? documentPath,
            string severity,
            string id,
            string message,
            int? startLine = null,
            int? startColumn = null,
            int? endLine = null,
            int? endColumn = null)
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

        public DocumentVersion(
            string documentId,
            string filePath,
            string contentHash,
            string encoding,
            string lineStart,
            DateTime createdAtUtc)
        {
            DocumentId = documentId;
            FilePath = filePath;
            ContentHash = contentHash;
            Encoding = encoding;
            LineStart = lineStart;
            CreatedAtUtc = createdAtUtc;
        }

        public DocumentVersion(
            string documentId,
            string filePath,
            string contentHash,
            string encoding,
            string lineStart,
            DateTime createdAtUtc,
            byte[] content,
            string lineStarts)
            : this(documentId, filePath, contentHash, encoding, lineStart, createdAtUtc)
        {
            Content = content;
            LineStarts = lineStarts;
        }
    }
}