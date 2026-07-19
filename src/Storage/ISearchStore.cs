namespace Lurp.Storage
{
    public interface ISearchStore
    {
        void BuildSearchIndex(string snapshotId);
        List<SourceSearchResult> SearchSource(string query, string snapshotId, int limit = 20, bool includeGenerated = false);
        List<SymbolSearchResult> SearchSymbols(string query, string snapshotId, int limit = 20, bool includeGenerated = false, string? kind = null);
        IndexedSymbolInfo? ResolveSymbolByFqn(string fqn, string snapshotId, bool includeGenerated = false);
    }
}
