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

    public class DocumentVersion
    {
        public string DocumentId { get; }
        public string FilePath { get; }
        public string ContentHash { get; }
        public string Encoding { get; }
        public string LineStart { get; }
        public DateTime CreatedAtUtc { get; }

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
    }
}