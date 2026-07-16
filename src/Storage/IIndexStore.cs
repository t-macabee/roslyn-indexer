using System;
using System.Collections.Generic;

namespace Lurp.Storage
{
    public sealed class SourceSearchResult
    {
        public string DocumentPath { get; }
        public string Snippet { get; }

        public SourceSearchResult(string documentPath, string snippet)
        {
            DocumentPath = documentPath ?? throw new ArgumentNullException(nameof(documentPath));
            Snippet = snippet ?? throw new ArgumentNullException(nameof(snippet));
        }
    }

    public sealed class SymbolSearchResult
    {
        public string SymbolId { get; }
        public string FullyQualifiedName { get; }
        public string Kind { get; }
        public string DocCommentId { get; }

        public SymbolSearchResult(string symbolId, string fullyQualifiedName, string kind, string docCommentId)
        {
            SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
            FullyQualifiedName = fullyQualifiedName ?? throw new ArgumentNullException(nameof(fullyQualifiedName));
            Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            DocCommentId = docCommentId ?? throw new ArgumentNullException(nameof(docCommentId));
        }
    }

    public enum ViewKind
    {
        Declaration,
        Signature,
        Body,
        Name,
        ContainingType,
        Surrounding,
    }

    public sealed class SymbolInfo
    {
        public SymbolId SymbolId { get; }
        public SymbolKind Kind { get; }
        public string? FullyQualifiedName { get; }
        public string? MetadataJson { get; }
        public int DeclarationCount { get; }
        public bool IsPartial { get; }

        public SymbolInfo(
            SymbolId symbolId,
            SymbolKind kind,
            string? fullyQualifiedName,
            string? metadataJson,
            int declarationCount,
            bool isPartial)
        {
            SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
            Kind = kind;
            FullyQualifiedName = fullyQualifiedName;
            MetadataJson = metadataJson;
            DeclarationCount = declarationCount;
            IsPartial = isPartial;
        }
    }

    public interface IIndexStore
    {
        void Open(string dbPath);
        void Close();
        bool IsOpen { get; }
        void RunMigrations();
        int GetCurrentSchemaVersion();
        void ValidateSchema(int expectedVersion);
        void SaveWorkspace(WorkspaceId id, string gitRoot, string solutionPath, DateTime createdAtUtc);
        void SaveSnapshot(SnapshotManifest manifest);
        SnapshotManifest? LoadLatestSnapshot(WorkspaceId workspaceId);
        string? GetSource(string relativePath, string snapshotId);

        void SaveDeclarations(string snapshotId, IEnumerable<SymbolDeclaration> declarations);
        SymbolInfo? GetSymbolInfo(string symbolId, string snapshotId);
        string? GetSymbolSource(string symbolId, string snapshotId, ViewKind viewKind, bool includeGenerated = false);
        string? GetContainingTypeSource(string symbolId, string snapshotId);
        string? GetSurroundingLines(string symbolId, string snapshotId, int contextLines);

        void BuildSearchIndex(string snapshotId);
        List<SourceSearchResult> SearchSource(string query, string snapshotId, int limit = 20, bool includeGenerated = false);
        List<SymbolSearchResult> SearchSymbols(string query, string snapshotId, int limit = 20, bool includeGenerated = false);
        SymbolInfo? ResolveSymbolByFqn(string fqn, string snapshotId, bool includeGenerated = false);

        
        void SaveEdges(string snapshotId, IEnumerable<EdgeRecord> edges);
        void SaveDiagnostics(string snapshotId, IEnumerable<DiagnosticRecord> diagnostics);
        void SaveAnnotations(string snapshotId, IEnumerable<AnnotationRecord> annotations);

        
        List<EdgeRecord> GetEdges(string snapshotId, string? symbolId = null);
        List<DiagnosticRecord> GetDiagnostics(string snapshotId, string? projectName = null);
        List<AnnotationRecord> GetAnnotations(string snapshotId, string? symbolId = null);

        
        List<EdgeRecord> GetEdgesByKind(string snapshotId, string kind);
        List<EdgeRecord> GetIncomingEdges(string snapshotId, string symbolId);
        List<EdgeRecord> GetOutgoingEdges(string snapshotId, string symbolId);

        
        void SaveSemanticChanges(string fromSnapshotId, string toSnapshotId, IEnumerable<SemanticChange> changes);
        List<SemanticChange> GetSemanticChanges(string fromSnapshotId, string toSnapshotId);
        List<string> GetSnapshotIds(string workspaceId);
        List<string> GetSymbolIdsInSnapshot(string snapshotId);
    }
}

