using Microsoft.Data.Sqlite;

#if CODE_ANALYSIS
using System.Diagnostics.CodeAnalysis;
#endif

namespace Lurp.Storage
{
    public class SqliteIndexStore : IIndexStore, IDisposable
    {
        private readonly string _dbPath;
        private SqliteConnection? _connection;

        private SnapshotLifecycleStore? _lifecycle;
        private SnapshotDocumentStore? _documents;
        private SnapshotSymbolStore? _symbols;
        private SnapshotPruner? _pruner;
        private SnapshotTimingStore? _timings;
        private DeclarationWriteStore? _declWriter;
        private DeclarationReadStore? _declReader;
        private DeclarationMaintenanceStore? _declMaintenance;
        private EdgeStore? _edgeStore;
        private SearchStore? _searchStore;
        private SemanticDiffStore? _semanticDiffStore;

        public SqliteIndexStore(string dbPath)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        }

        // ── Connection lifecycle ────────────────────────────────────────────

        public bool IsOpen => _connection != null;

        public void Open(string dbPath)
        {
            if (_connection != null)
                return;

            _connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            _connection.Open();

            _lifecycle = new SnapshotLifecycleStore(_connection);
            _documents = new SnapshotDocumentStore(_connection);
            _symbols = new SnapshotSymbolStore(_connection);
            _pruner = new SnapshotPruner(_connection);
            _timings = new SnapshotTimingStore(_connection);
            _declWriter = new DeclarationWriteStore(_connection);
            _declReader = new DeclarationReadStore(_connection);
            _declMaintenance = new DeclarationMaintenanceStore(_connection);
            _edgeStore = new EdgeStore(_connection);
            _searchStore = new SearchStore(_connection);
            _semanticDiffStore = new SemanticDiffStore(_connection);
        }

        public void Close()
        {
            if (_connection == null)
                return;

            _connection.Close();
            _connection.Dispose();
            _connection = null;

            _lifecycle = null;
            _documents = null;
            _symbols = null;
            _pruner = null;
            _timings = null;
            _declWriter = null;
            _declReader = null;
            _declMaintenance = null;
            _edgeStore = null;
            _searchStore = null;
            _semanticDiffStore = null;
        }

        private void EnsureOpen()
        {
            if (_connection == null)
                throw new InvalidOperationException("Store is not open. Call Open() first.");
        }

        public void Dispose()
        {
            Close();
        }

        // ── Migrations (use their own connection) ──────────────────────────

        public void RunMigrations()
        {
            new MigrationRunner(_dbPath).RunMigrations();
        }

        public int GetCurrentSchemaVersion()
        {
            return new MigrationRunner(_dbPath).GetCurrentSchemaVersion();
        }

        public void ValidateSchema(int expectedVersion)
        {
            var actual = GetCurrentSchemaVersion();
            if (actual != expectedVersion)
                throw new InvalidOperationException($"Schema version mismatch: expected {expectedVersion}, got {actual}.");
        }

        // ── ISnapshotStore ──────────────────────────────────────────────────

        public void SaveWorkspace(string id, string gitRoot, string solutionPath, DateTime createdAtUtc)
            { EnsureOpen(); _lifecycle!.SaveWorkspace(id, gitRoot, solutionPath, createdAtUtc); }
        public void SaveSnapshot(SnapshotRow manifest)
            { EnsureOpen(); _lifecycle!.SaveSnapshot(manifest); }
        public void MarkSnapshotInProgress(string snapshotId)
            { EnsureOpen(); _lifecycle!.MarkSnapshotInProgress(snapshotId); }
        public void MarkSnapshotComplete(string snapshotId)
            { EnsureOpen(); _lifecycle!.MarkSnapshotComplete(snapshotId); }
        public SnapshotRow? LoadLatestSnapshot(string workspaceId)
            { EnsureOpen(); return _lifecycle!.LoadLatestSnapshot(workspaceId); }
        public string? GetLatestSnapshotId(string? workspaceId = null)
            { EnsureOpen(); return _lifecycle!.GetLatestSnapshotId(workspaceId); }
        public List<string> GetSnapshotIds(string workspaceId)
            { EnsureOpen(); return _lifecycle!.GetSnapshotIds(workspaceId); }
        public string? GetSource(string relativePath, string snapshotId)
            { EnsureOpen(); return _documents!.GetSource(relativePath, snapshotId); }
        public void SaveSnapshotDocuments(string snapshotId, IEnumerable<(string DocumentId, string DocumentVersionId)> entries)
            { EnsureOpen(); _documents!.SaveSnapshotDocuments(snapshotId, entries); }
        public Dictionary<string, string> GetDocumentVersionIdsByPath(string snapshotId)
            { EnsureOpen(); return _documents!.GetDocumentVersionIdsByPath(snapshotId); }
        public List<string> GetDocumentVersionIdsForDocuments(string snapshotId, IEnumerable<string> documentPaths)
            { EnsureOpen(); return _documents!.GetDocumentVersionIdsForDocuments(snapshotId, documentPaths); }
        public void SaveSnapshotSymbols(string snapshotId, IEnumerable<string> symbolIds)
            { EnsureOpen(); _symbols!.SaveSnapshotSymbols(snapshotId, symbolIds); }
        public void CopySnapshotSymbols(string fromSnapshotId, string toSnapshotId)
            { EnsureOpen(); _symbols!.CopySnapshotSymbols(fromSnapshotId, toSnapshotId); }
        public void DeleteSnapshotSymbolsBySymbolIds(string snapshotId, IEnumerable<string> symbolIds)
            { EnsureOpen(); _symbols!.DeleteSnapshotSymbolsBySymbolIds(snapshotId, symbolIds); }
        public List<string> GetSymbolIdsInSnapshot(string snapshotId)
            { EnsureOpen(); return _symbols!.GetSymbolIdsInSnapshot(snapshotId); }
        public void PruneOldSnapshots(int keep = 3)
            { EnsureOpen(); _pruner!.PruneOldSnapshots(keep); }
        public void SaveTimings(string snapshotId, IEnumerable<SnapshotTimingRow> timings)
            { EnsureOpen(); _timings!.SaveTimings(snapshotId, timings); }
        public List<SnapshotTimingRow> GetTimings(string snapshotId)
            { EnsureOpen(); return _timings!.GetTimings(snapshotId); }

        // ── IDeclarationStore ──────────────────────────────────────────────

        public void SaveDeclarations(string snapshotId, IEnumerable<SymbolDeclaration> declarations)
            { EnsureOpen(); _declWriter!.SaveDeclarations(snapshotId, declarations); }
        public IndexedSymbolInfo? GetSymbolInfo(string symbolId, string snapshotId)
            { EnsureOpen(); return _declReader!.GetSymbolInfo(symbolId, snapshotId); }
        public string? GetSymbolSource(string symbolId, string snapshotId, ViewKind viewKind, bool includeGenerated = false)
            { EnsureOpen(); return _declReader!.GetSymbolSource(symbolId, snapshotId, viewKind, includeGenerated); }
        public string? GetContainingTypeSource(string symbolId, string snapshotId)
            { EnsureOpen(); return _declReader!.GetContainingTypeSource(symbolId, snapshotId); }
        public string? GetSurroundingLines(string symbolId, string snapshotId, int contextLines)
            { EnsureOpen(); return _declReader!.GetSurroundingLines(symbolId, snapshotId, contextLines); }
        public void DeleteDeclarationsByDocumentVersionIds(IEnumerable<string> documentVersionIds)
            { EnsureOpen(); _declMaintenance!.DeleteDeclarationsByDocumentVersionIds(documentVersionIds); }
        public List<string> GetSymbolIdsByDocumentVersionIds(string snapshotId, IEnumerable<string> documentVersionIds)
            { EnsureOpen(); return _declMaintenance!.GetSymbolIdsByDocumentVersionIds(snapshotId, documentVersionIds); }
        public string? ResolveSymbolByLocation(string relativePath, int line, string snapshotId, bool includeGenerated = false)
            { EnsureOpen(); return _declMaintenance!.ResolveSymbolByLocation(relativePath, line, snapshotId, includeGenerated); }

        // ── IEdgeStore ─────────────────────────────────────────────────────

        public void SaveEdges(string snapshotId, IEnumerable<EdgeRecord> edges)
            { EnsureOpen(); _edgeStore!.SaveEdges(snapshotId, edges); }
        public void SaveDiagnostics(string snapshotId, IEnumerable<DiagnosticRecord> diagnostics)
            { EnsureOpen(); _edgeStore!.SaveDiagnostics(snapshotId, diagnostics); }
        public void SaveAnnotations(string snapshotId, IEnumerable<AnnotationRecord> annotations)
            { EnsureOpen(); _edgeStore!.SaveAnnotations(snapshotId, annotations); }
        public List<EdgeRecord> GetEdges(string snapshotId, string? symbolId = null)
            { EnsureOpen(); return _edgeStore!.GetEdges(snapshotId, symbolId); }
        public List<DiagnosticRecord> GetDiagnostics(string snapshotId, string? projectName = null)
            { EnsureOpen(); return _edgeStore!.GetDiagnostics(snapshotId, projectName); }
        public List<AnnotationRecord> GetAnnotations(string snapshotId, string? symbolId = null)
            { EnsureOpen(); return _edgeStore!.GetAnnotations(snapshotId, symbolId); }
        public List<EdgeRecord> GetEdgesByKind(string snapshotId, string kind)
            { EnsureOpen(); return _edgeStore!.GetEdgesByKind(snapshotId, kind); }
        public List<EdgeRecord> GetIncomingEdges(string snapshotId, string symbolId)
            { EnsureOpen(); return _edgeStore!.GetIncomingEdges(snapshotId, symbolId); }
        public List<EdgeRecord> GetOutgoingEdges(string snapshotId, string symbolId)
            { EnsureOpen(); return _edgeStore!.GetOutgoingEdges(snapshotId, symbolId); }
        public void DeleteEdgesByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths)
            { EnsureOpen(); _edgeStore!.DeleteEdgesByDocumentPaths(snapshotId, documentPaths); }
        public void DeleteEdgesWithNullDocumentPathForAssemblies(string snapshotId, IEnumerable<string> assemblyIdentities)
            { EnsureOpen(); _edgeStore!.DeleteEdgesWithNullDocumentPathForAssemblies(snapshotId, assemblyIdentities); }
        public void DeleteEdgesWithNullDocumentPathForSymbols(string snapshotId, IEnumerable<string> symbolIds)
            { EnsureOpen(); _edgeStore!.DeleteEdgesWithNullDocumentPathForSymbols(snapshotId, symbolIds); }
        public void CopyEdgesToSnapshot(string fromSnapshotId, string toSnapshotId)
            { EnsureOpen(); _edgeStore!.CopyEdgesToSnapshot(fromSnapshotId, toSnapshotId); }
        public void CopySnapshotDiagnostics(string fromSnapshotId, string toSnapshotId)
            { EnsureOpen(); _edgeStore!.CopySnapshotDiagnostics(fromSnapshotId, toSnapshotId); }
        public void DeleteDiagnosticsByProjectNames(string snapshotId, IEnumerable<string> projectNames)
            { EnsureOpen(); _edgeStore!.DeleteDiagnosticsByProjectNames(snapshotId, projectNames); }
        public void UpsertExtractors(IEnumerable<(string Name, string Version, string Description)> extractors)
            { EnsureOpen(); _edgeStore!.UpsertExtractors(extractors); }

        // ── ISearchStore ───────────────────────────────────────────────────

        public void BuildSearchIndex(string snapshotId)
            { EnsureOpen(); _searchStore!.BuildSearchIndex(snapshotId); }
        public void BuildSearchIndex(string snapshotId, HashSet<string> changedDocumentPaths, HashSet<string> changedSymbolIds)
            { EnsureOpen(); _searchStore!.BuildSearchIndex(snapshotId, changedDocumentPaths, changedSymbolIds); }
        public void CopySearchIndexToSnapshot(string fromSnapshotId, string toSnapshotId)
            { EnsureOpen(); _searchStore!.CopySearchIndexToSnapshot(fromSnapshotId, toSnapshotId); }
        public List<SourceSearchResult> SearchSource(string query, string snapshotId, int limit = 20, bool includeGenerated = false, int snippetTokens = 64)
            { EnsureOpen(); return _searchStore!.SearchSource(query, snapshotId, limit, includeGenerated, snippetTokens); }
        public List<SymbolSearchResult> SearchSymbols(string query, string snapshotId, int limit = 20, bool includeGenerated = false, string? kind = null)
            { EnsureOpen(); return _searchStore!.SearchSymbols(query, snapshotId, limit, includeGenerated, kind); }
        public IndexedSymbolInfo? ResolveSymbolByFqn(string fqn, string snapshotId, bool includeGenerated = false)
            { EnsureOpen(); return _searchStore!.ResolveSymbolByFqn(fqn, snapshotId, includeGenerated); }

        // ── ISemanticDiffStore ─────────────────────────────────────────────

        public void SaveSemanticChanges(string fromSnapshotId, string toSnapshotId, IEnumerable<SemanticChange> changes)
            { EnsureOpen(); _semanticDiffStore!.SaveSemanticChanges(fromSnapshotId, toSnapshotId, changes); }
        public List<SemanticChange> GetSemanticChanges(string fromSnapshotId, string toSnapshotId)
            { EnsureOpen(); return _semanticDiffStore!.GetSemanticChanges(fromSnapshotId, toSnapshotId); }
    }
}
