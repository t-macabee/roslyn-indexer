namespace Lurp.Storage
{
    public interface ISearchStore
    {
        void BuildSearchIndex(string snapshotId);
        void BuildSearchIndex(string snapshotId, HashSet<string> changedDocumentPaths, HashSet<string> changedSymbolIds);
        void CopySearchIndexToSnapshot(string fromSnapshotId, string toSnapshotId);
        List<SourceSearchResult> SearchSource(string query, string snapshotId, int limit = 20, bool includeGenerated = false, int snippetTokens = 64);
        List<SymbolSearchResult> SearchSymbols(string query, string snapshotId, int limit = 20, bool includeGenerated = false, string? kind = null);
        IndexedSymbolInfo? ResolveSymbolByFqn(string fqn, string snapshotId, bool includeGenerated = false);
    }
}
