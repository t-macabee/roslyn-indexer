namespace Lurp.Storage
{
    public class SqliteIndexStore : IIndexStore
    {
        private readonly SnapshotStore _snapshotStore;
        private readonly DeclarationStore _declarationStore;
        private readonly EdgeStore _edgeStore;
        private readonly SearchStore _searchStore;
        private readonly SemanticDiffStore _semanticDiffStore;

        public SqliteIndexStore(string dbPath)
        {
            _snapshotStore = new SnapshotStore(dbPath);
            _declarationStore = new DeclarationStore(dbPath);
            _edgeStore = new EdgeStore(dbPath);
            _searchStore = new SearchStore(dbPath);
            _semanticDiffStore = new SemanticDiffStore(dbPath);
        }

        // ── ISnapshotStore ──────────────────────────────────────────────────

        public bool IsOpen => _snapshotStore.IsOpen;
        public void Open(string dbPath) => _snapshotStore.Open(dbPath);
        public void Close() => _snapshotStore.Close();
        public void RunMigrations() => _snapshotStore.RunMigrations();
        public int GetCurrentSchemaVersion() => _snapshotStore.GetCurrentSchemaVersion();
        public void ValidateSchema(int expectedVersion) => _snapshotStore.ValidateSchema(expectedVersion);

        public void SaveWorkspace(string id, string gitRoot, string solutionPath, DateTime createdAtUtc)
            => _snapshotStore.SaveWorkspace(id, gitRoot, solutionPath, createdAtUtc);
        public void SaveSnapshot(Storage.SnapshotManifest manifest) => _snapshotStore.SaveSnapshot(manifest);
        public void MarkSnapshotInProgress(string snapshotId) => _snapshotStore.MarkSnapshotInProgress(snapshotId);
        public void MarkSnapshotComplete(string snapshotId) => _snapshotStore.MarkSnapshotComplete(snapshotId);
        public Storage.SnapshotManifest? LoadLatestSnapshot(string workspaceId) => _snapshotStore.LoadLatestSnapshot(workspaceId);
        public string? GetLatestSnapshotId(string? workspaceId = null) => _snapshotStore.GetLatestSnapshotId(workspaceId);
        public string? GetSource(string relativePath, string snapshotId) => _snapshotStore.GetSource(relativePath, snapshotId);
        public List<string> GetSnapshotIds(string workspaceId) => _snapshotStore.GetSnapshotIds(workspaceId);
        public void SaveSnapshotDocuments(string snapshotId, IEnumerable<(string DocumentId, string DocumentVersionId)> entries)
            => _snapshotStore.SaveSnapshotDocuments(snapshotId, entries);
        public Dictionary<string, string> GetDocumentVersionIdsByPath(string snapshotId)
            => _snapshotStore.GetDocumentVersionIdsByPath(snapshotId);
        public List<string> GetDocumentVersionIdsForDocuments(string snapshotId, IEnumerable<string> documentPaths)
            => _snapshotStore.GetDocumentVersionIdsForDocuments(snapshotId, documentPaths);
        public void SaveSnapshotSymbols(string snapshotId, IEnumerable<string> symbolIds)
            => _snapshotStore.SaveSnapshotSymbols(snapshotId, symbolIds);
        public void CopySnapshotSymbols(string fromSnapshotId, string toSnapshotId)
            => _snapshotStore.CopySnapshotSymbols(fromSnapshotId, toSnapshotId);
        public void DeleteSnapshotSymbolsBySymbolIds(string snapshotId, IEnumerable<string> symbolIds)
            => _snapshotStore.DeleteSnapshotSymbolsBySymbolIds(snapshotId, symbolIds);
        public List<string> GetSymbolIdsInSnapshot(string snapshotId) => _snapshotStore.GetSymbolIdsInSnapshot(snapshotId);
        public void PruneOldSnapshots(int keep = 3) => _snapshotStore.PruneOldSnapshots(keep);

        // ── IDeclarationStore ──────────────────────────────────────────────

        public void SaveDeclarations(string snapshotId, IEnumerable<SymbolDeclaration> declarations)
            => _declarationStore.SaveDeclarations(snapshotId, declarations);
        public SymbolInfo? GetSymbolInfo(string symbolId, string snapshotId)
            => _declarationStore.GetSymbolInfo(symbolId, snapshotId);
        public string? GetSymbolSource(string symbolId, string snapshotId, ViewKind viewKind, bool includeGenerated = false)
            => _declarationStore.GetSymbolSource(symbolId, snapshotId, viewKind, includeGenerated);
        public string? GetContainingTypeSource(string symbolId, string snapshotId)
            => _declarationStore.GetContainingTypeSource(symbolId, snapshotId);
        public string? GetSurroundingLines(string symbolId, string snapshotId, int contextLines)
            => _declarationStore.GetSurroundingLines(symbolId, snapshotId, contextLines);
        public void DeleteDeclarationsByDocumentVersionIds(IEnumerable<string> documentVersionIds)
            => _declarationStore.DeleteDeclarationsByDocumentVersionIds(documentVersionIds);
        public List<string> GetSymbolIdsByDocumentVersionIds(string snapshotId, IEnumerable<string> documentVersionIds)
            => _declarationStore.GetSymbolIdsByDocumentVersionIds(snapshotId, documentVersionIds);
        public string? ResolveSymbolByLocation(string relativePath, int line, string snapshotId, bool includeGenerated = false)
            => _declarationStore.ResolveSymbolByLocation(relativePath, line, snapshotId, includeGenerated);

        // ── IEdgeStore ─────────────────────────────────────────────────────

        public void SaveEdges(string snapshotId, IEnumerable<EdgeRecord> edges)
            => _edgeStore.SaveEdges(snapshotId, edges);
        public void SaveDiagnostics(string snapshotId, IEnumerable<DiagnosticRecord> diagnostics)
            => _edgeStore.SaveDiagnostics(snapshotId, diagnostics);
        public void SaveAnnotations(string snapshotId, IEnumerable<AnnotationRecord> annotations)
            => _edgeStore.SaveAnnotations(snapshotId, annotations);
        public List<EdgeRecord> GetEdges(string snapshotId, string? symbolId = null)
            => _edgeStore.GetEdges(snapshotId, symbolId);
        public List<DiagnosticRecord> GetDiagnostics(string snapshotId, string? projectName = null)
            => _edgeStore.GetDiagnostics(snapshotId, projectName);
        public List<AnnotationRecord> GetAnnotations(string snapshotId, string? symbolId = null)
            => _edgeStore.GetAnnotations(snapshotId, symbolId);
        public List<EdgeRecord> GetEdgesByKind(string snapshotId, string kind)
            => _edgeStore.GetEdgesByKind(snapshotId, kind);
        public List<EdgeRecord> GetIncomingEdges(string snapshotId, string symbolId)
            => _edgeStore.GetIncomingEdges(snapshotId, symbolId);
        public List<EdgeRecord> GetOutgoingEdges(string snapshotId, string symbolId)
            => _edgeStore.GetOutgoingEdges(snapshotId, symbolId);
        public void DeleteEdgesByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths)
            => _edgeStore.DeleteEdgesByDocumentPaths(snapshotId, documentPaths);
        public void DeleteEdgesWithNullDocumentPathForAssemblies(string snapshotId, IEnumerable<string> assemblyIdentities)
            => _edgeStore.DeleteEdgesWithNullDocumentPathForAssemblies(snapshotId, assemblyIdentities);
        public void CopyEdgesToSnapshot(string fromSnapshotId, string toSnapshotId)
            => _edgeStore.CopyEdgesToSnapshot(fromSnapshotId, toSnapshotId);
        public void CopySnapshotDiagnostics(string fromSnapshotId, string toSnapshotId)
            => _edgeStore.CopySnapshotDiagnostics(fromSnapshotId, toSnapshotId);
        public void DeleteDiagnosticsByProjectNames(string snapshotId, IEnumerable<string> projectNames)
            => _edgeStore.DeleteDiagnosticsByProjectNames(snapshotId, projectNames);

        // ── ISearchStore ───────────────────────────────────────────────────

        public void BuildSearchIndex(string snapshotId) => _searchStore.BuildSearchIndex(snapshotId);
        public List<SourceSearchResult> SearchSource(string query, string snapshotId, int limit = 20, bool includeGenerated = false)
            => _searchStore.SearchSource(query, snapshotId, limit, includeGenerated);
        public List<SymbolSearchResult> SearchSymbols(string query, string snapshotId, int limit = 20, bool includeGenerated = false, string? kind = null)
            => _searchStore.SearchSymbols(query, snapshotId, limit, includeGenerated, kind);
        public SymbolInfo? ResolveSymbolByFqn(string fqn, string snapshotId, bool includeGenerated = false)
            => _searchStore.ResolveSymbolByFqn(fqn, snapshotId, includeGenerated);

        // ── ISemanticDiffStore ─────────────────────────────────────────────

        public void SaveSemanticChanges(string fromSnapshotId, string toSnapshotId, IEnumerable<SemanticChange> changes)
            => _semanticDiffStore.SaveSemanticChanges(fromSnapshotId, toSnapshotId, changes);
        public List<SemanticChange> GetSemanticChanges(string fromSnapshotId, string toSnapshotId)
            => _semanticDiffStore.GetSemanticChanges(fromSnapshotId, toSnapshotId);
    }
}
