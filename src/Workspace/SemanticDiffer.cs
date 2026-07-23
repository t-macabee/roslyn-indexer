using Lurp.Storage;
using System.Text.Json;

namespace Lurp.Workspace
{

    public class SemanticDiffer
    {
        private readonly ISnapshotStore _snapshotStore;
        private readonly IDeclarationStore _declarationStore;
        private readonly IEdgeStore _edgeStore;

        public SemanticDiffer(ISnapshotStore snapshotStore, IDeclarationStore declarationStore, IEdgeStore edgeStore)
        {
            _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
            _declarationStore = declarationStore ?? throw new ArgumentNullException(nameof(declarationStore));
            _edgeStore = edgeStore ?? throw new ArgumentNullException(nameof(edgeStore));
        }

        public (List<SemanticChange> Changes, int SkippedComparisons) ComputeDiff(string fromSnapshotId, string toSnapshotId)
        {
            var changes = new List<SemanticChange>();
            int skippedComparisons = 0;

            var fromSymbols = GetSymbolIdsInSnapshot(fromSnapshotId);
            var toSymbols = GetSymbolIdsInSnapshot(toSnapshotId);

            var fromSet = new HashSet<string>(fromSymbols);
            var toSet = new HashSet<string>(toSymbols);

            foreach (var symbolId in toSymbols)
            {
                if (!fromSet.Contains(symbolId))
                {
                    changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.SymbolAdded, symbolId, new { symbol_id = symbolId }));
                }
            }

            foreach (var symbolId in fromSymbols)
            {
                if (!toSet.Contains(symbolId))
                {
                    changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.SymbolRemoved, symbolId, new { symbol_id = symbolId }));
                }
            }

            var common = fromSet.Intersect(toSet).ToList();

            foreach (var symbolId in common)
            {
                var fromInfo = _declarationStore.GetSymbolInfo(symbolId, fromSnapshotId);
                var toInfo = _declarationStore.GetSymbolInfo(symbolId, toSnapshotId);

                if (fromInfo == null || toInfo == null)
                {
                    skippedComparisons++;
                    changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.ComparisonUnavailable, symbolId,
                        new { reason = $"Symbol info missing: from={(fromInfo == null ? "missing" : "present")}, to={(toInfo == null ? "missing" : "present")}" }));
                    continue;
                }

                if (!string.Equals(fromInfo.FullyQualifiedName, toInfo.FullyQualifiedName, StringComparison.Ordinal) &&
                    fromInfo.SymbolId.DocCommentId == toInfo.SymbolId.DocCommentId)
                {
                    var fromSimple = GetSimpleName(fromInfo.FullyQualifiedName);
                    var toSimple = GetSimpleName(toInfo.FullyQualifiedName);
                    var fromContainer = GetContainer(fromInfo.FullyQualifiedName);
                    var toContainer = GetContainer(toInfo.FullyQualifiedName);

                    if (fromSimple != toSimple)
                    {
                        changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.SymbolRenamed, symbolId, new { before = fromInfo.FullyQualifiedName, after = toInfo.FullyQualifiedName }));
                    }

                    if (fromContainer != toContainer)
                    {
                        changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.SymbolMoved, symbolId, new { before = fromContainer, after = toContainer }));
                    }
                }

                var metaChanges = CompareMetadata(symbolId, fromInfo.MetadataJson, toInfo.MetadataJson, fromSnapshotId, toSnapshotId);
                changes.AddRange(metaChanges);

                var (sourceChanges, sourceSkipped) = CompareSource(symbolId, fromSnapshotId, toSnapshotId);
                changes.AddRange(sourceChanges);
                skippedComparisons += sourceSkipped;
            }

            var fromEdges = _edgeStore.GetEdges(fromSnapshotId);
            var toEdges = _edgeStore.GetEdges(toSnapshotId);

            DiffEdges(fromEdges, toEdges, fromSnapshotId, toSnapshotId, changes);

            return (changes, skippedComparisons);
        }

        public (List<SemanticChange> Changes, int SkippedComparisons) ComputeDiff(string fromSnapshotId, string toSnapshotId, HashSet<string> changedPaths, HashSet<string> changedSymbolIds)
        {
            var changes = new List<SemanticChange>();
            int skippedComparisons = 0;

            if (changedSymbolIds.Count == 0)
                return (changes, skippedComparisons);

            var fromSymbols = GetSymbolIdsInSnapshot(fromSnapshotId);
            var toSymbols = GetSymbolIdsInSnapshot(toSnapshotId);

            var fromSet = new HashSet<string>(fromSymbols);
            var toSet = new HashSet<string>(toSymbols);

            // Only consider symbols in the changed neighborhood
            foreach (var symbolId in toSymbols)
            {
                if (!changedSymbolIds.Contains(symbolId))
                    continue;
                if (!fromSet.Contains(symbolId))
                    changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.SymbolAdded, symbolId, new { symbol_id = symbolId }));
            }

            foreach (var symbolId in fromSymbols)
            {
                if (!changedSymbolIds.Contains(symbolId))
                    continue;
                if (!toSet.Contains(symbolId))
                    changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.SymbolRemoved, symbolId, new { symbol_id = symbolId }));
            }

            var common = fromSet.Intersect(toSet).Where(id => changedSymbolIds.Contains(id)).ToList();

            foreach (var symbolId in common)
            {
                var fromInfo = _declarationStore.GetSymbolInfo(symbolId, fromSnapshotId);
                var toInfo = _declarationStore.GetSymbolInfo(symbolId, toSnapshotId);

                if (fromInfo == null || toInfo == null)
                {
                    skippedComparisons++;
                    changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.ComparisonUnavailable, symbolId,
                        new { reason = $"Symbol info missing: from={(fromInfo == null ? "missing" : "present")}, to={(toInfo == null ? "missing" : "present")}" }));
                    continue;
                }

                if (!string.Equals(fromInfo.FullyQualifiedName, toInfo.FullyQualifiedName, StringComparison.Ordinal) &&
                    fromInfo.SymbolId.DocCommentId == toInfo.SymbolId.DocCommentId)
                {
                    var fromSimple = GetSimpleName(fromInfo.FullyQualifiedName);
                    var toSimple = GetSimpleName(toInfo.FullyQualifiedName);
                    var fromContainer = GetContainer(fromInfo.FullyQualifiedName);
                    var toContainer = GetContainer(toInfo.FullyQualifiedName);

                    if (fromSimple != toSimple)
                        changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.SymbolRenamed, symbolId, new { before = fromInfo.FullyQualifiedName, after = toInfo.FullyQualifiedName }));

                    if (fromContainer != toContainer)
                        changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.SymbolMoved, symbolId, new { before = fromContainer, after = toContainer }));
                }

                var metaChanges = CompareMetadata(symbolId, fromInfo.MetadataJson, toInfo.MetadataJson, fromSnapshotId, toSnapshotId);
                changes.AddRange(metaChanges);

                var (sourceChanges, sourceSkipped) = CompareSource(symbolId, fromSnapshotId, toSnapshotId);
                changes.AddRange(sourceChanges);
                skippedComparisons += sourceSkipped;
            }

            // Edge diff scoped to changed-symbol neighborhood:
            // only consider edges where source or target is in changedSymbolIds.
            var fromEdges = _edgeStore.GetEdges(fromSnapshotId)
                .Where(e => changedSymbolIds.Contains(e.SourceSymbolId) || changedSymbolIds.Contains(e.TargetSymbolId))
                .ToList();
            var toEdges = _edgeStore.GetEdges(toSnapshotId)
                .Where(e => changedSymbolIds.Contains(e.SourceSymbolId) || changedSymbolIds.Contains(e.TargetSymbolId))
                .ToList();

            DiffEdges(fromEdges, toEdges, fromSnapshotId, toSnapshotId, changes);

            return (changes, skippedComparisons);
        }

        private void DiffEdges(List<EdgeRecord> fromEdges, List<EdgeRecord> toEdges, string fromSnapshotId, string toSnapshotId, List<SemanticChange> changes)
        {
            var fromEdgeSet = new HashSet<(string source, string target, string kind)>(fromEdges.Select(e => (e.SourceSymbolId, e.TargetSymbolId, e.Kind)));
            var toEdgeSet = new HashSet<(string source, string target, string kind)>(toEdges.Select(e => (e.SourceSymbolId, e.TargetSymbolId, e.Kind)));

            foreach (var edge in toEdges)
            {
                var key = (edge.SourceSymbolId, edge.TargetSymbolId, edge.Kind);
                if (!fromEdgeSet.Contains(key))
                {
                    changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.EdgeAdded, edge.SourceSymbolId, new { source = edge.SourceSymbolId, target = edge.TargetSymbolId, kind = edge.Kind }));
                }
            }

            foreach (var edge in fromEdges)
            {
                var key = (edge.SourceSymbolId, edge.TargetSymbolId, edge.Kind);
                if (!toEdgeSet.Contains(key))
                {
                    changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.EdgeRemoved, edge.SourceSymbolId, new { source = edge.SourceSymbolId, target = edge.TargetSymbolId, kind = edge.Kind }));
                }
            }
        }

        private List<string> GetSymbolIdsInSnapshot(string snapshotId)
        {
            return _snapshotStore.GetSymbolIdsInSnapshot(snapshotId);
        }

        private List<SemanticChange> CompareMetadata(string symbolId, string? fromJson, string? toJson, string fromSnapshotId, string toSnapshotId)
        {
            var changes = new List<SemanticChange>();

            var fromMeta = string.IsNullOrEmpty(fromJson)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(fromJson) ?? [];

            var toMeta = string.IsNullOrEmpty(toJson)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(toJson) ?? [];

            var fromAcc = GetMetaString(fromMeta, "accessibility");
            var toAcc = GetMetaString(toMeta, "accessibility");
            if (fromAcc != null && toAcc != null && fromAcc != toAcc)
            {
                changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.AccessibilityChanged, symbolId, new { before = fromAcc, after = toAcc }));
            }

            var fromSig = GetMetaString(fromMeta, "signature");
            var toSig = GetMetaString(toMeta, "signature");
            if (fromSig != null && toSig != null && fromSig != toSig)
            {
                changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.SignatureChanged, symbolId, new { before = fromSig, after = toSig }));
            }

            var fromBase = GetMetaString(fromMeta, "base_type");
            var toBase = GetMetaString(toMeta, "base_type");
            if (fromBase != null && toBase != null && fromBase != toBase)
            {
                changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.BaseTypeChanged, symbolId, new { before = fromBase, after = toBase }));
            }

            var fromAttrs = GetMetaArray(fromMeta, "attributes");
            var toAttrs = GetMetaArray(toMeta, "attributes");
            if (fromAttrs != null && toAttrs != null && !fromAttrs.SequenceEqual(toAttrs))
            {
                changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.AttributeChanged, symbolId, new { before = fromAttrs, after = toAttrs }));
            }

            return changes;
        }

        private (List<SemanticChange> Changes, int Skipped) CompareSource(string symbolId, string fromSnapshotId, string toSnapshotId)
        {
            var changes = new List<SemanticChange>();

            var fromSig = _declarationStore.GetSymbolSource(symbolId, fromSnapshotId, ViewKind.Signature);
            var toSig = _declarationStore.GetSymbolSource(symbolId, toSnapshotId, ViewKind.Signature);

            if (fromSig == null || toSig == null)
            {
                if (fromSig == null && toSig == null)
                    return (changes, 0);

                changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.ComparisonUnavailable, symbolId,
                    new { reason = $"Source comparison unavailable: from_signature={(fromSig == null ? "missing" : "present")}, to_signature={(toSig == null ? "missing" : "present")}" }));
                return (changes, 1);
            }

            var fromBody = _declarationStore.GetSymbolSource(symbolId, fromSnapshotId, ViewKind.Body);
            var toBody = _declarationStore.GetSymbolSource(symbolId, toSnapshotId, ViewKind.Body);

            if (fromSig == toSig)
            {
                if (fromBody != toBody)
                {
                    changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.BodyOnlyChanged, symbolId, new { note = "signature unchanged, body differs" }));
                }
            }

            return (changes, 0);
        }

        private static string GetSimpleName(string? fqn)
        {
            if (string.IsNullOrEmpty(fqn)) return string.Empty;
            var idx = fqn.LastIndexOf('.');
            return idx < 0 ? fqn : fqn.Substring(idx + 1);
        }

        private static string GetContainer(string? fqn)
        {
            if (string.IsNullOrEmpty(fqn)) return string.Empty;
            var idx = fqn.LastIndexOf('.');
            return idx < 0 ? string.Empty : fqn.Substring(0, idx);
        }

        private static string? GetMetaString(Dictionary<string, JsonElement> meta, string key)
        {
            if (meta.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
            return null;
        }

        private static List<string>? GetMetaArray(Dictionary<string, JsonElement> meta, string key)
        {
            if (meta.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Array)
            {
                return el.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
            }
            return null;
        }

        private static SemanticChange MakeChange(string? fromSnapshotId, string? toSnapshotId, string changeType, string symbolId, object? detail)
        {
            return new SemanticChange
            {
                ChangeId = Guid.NewGuid().ToString("N"),
                FromSnapshotId = fromSnapshotId ?? string.Empty,
                ToSnapshotId = toSnapshotId ?? string.Empty,
                ChangeType = changeType,
                SymbolId = symbolId,
                DetailJson = detail != null ? JsonSerializer.Serialize(detail) : null,
                CreatedAtUtc = DateTime.UtcNow
            };
        }
    }
}
