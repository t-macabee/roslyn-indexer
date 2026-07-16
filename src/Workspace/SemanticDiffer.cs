using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Workspace
{
    
    
    
    
    public class SemanticDiffer
    {
        private readonly IIndexStore _store;

        public SemanticDiffer(IIndexStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        
        
        
        public List<SemanticChange> ComputeDiff(string fromSnapshotId, string toSnapshotId)
        {
            var changes = new List<SemanticChange>();

            var fromSymbols = GetSymbolIdsInSnapshot(fromSnapshotId);
            var toSymbols = GetSymbolIdsInSnapshot(toSnapshotId);

            var fromSet = new HashSet<string>(fromSymbols);
            var toSet = new HashSet<string>(toSymbols);

            
            foreach (var symbolId in toSymbols)
            {
                if (!fromSet.Contains(symbolId))
                {
                    changes.Add(MakeChange(
                        fromSnapshotId, toSnapshotId,
                        ChangeType.SymbolAdded, symbolId,
                        new { symbol_id = symbolId }));
                }
            }

            
            foreach (var symbolId in fromSymbols)
            {
                if (!toSet.Contains(symbolId))
                {
                    changes.Add(MakeChange(
                        fromSnapshotId, toSnapshotId,
                        ChangeType.SymbolRemoved, symbolId,
                        new { symbol_id = symbolId }));
                }
            }

            
            var common = fromSet.Intersect(toSet).ToList();

            foreach (var symbolId in common)
            {
                var fromInfo = _store.GetSymbolInfo(symbolId, fromSnapshotId);
                var toInfo = _store.GetSymbolInfo(symbolId, toSnapshotId);

                if (fromInfo == null || toInfo == null)
                    continue;

                
                if (!string.Equals(fromInfo.FullyQualifiedName, toInfo.FullyQualifiedName, StringComparison.Ordinal) &&
                    fromInfo.SymbolId.DocCommentId == toInfo.SymbolId.DocCommentId)
                {
                    changes.Add(MakeChange(
                        fromSnapshotId, toSnapshotId,
                        ChangeType.SymbolRenamed, symbolId,
                        new { before = fromInfo.FullyQualifiedName, after = toInfo.FullyQualifiedName }));
                }

                
                var metaChanges = CompareMetadata(symbolId, fromInfo.MetadataJson, toInfo.MetadataJson, fromSnapshotId, toSnapshotId);
                changes.AddRange(metaChanges);

                
                var sourceChanges = CompareSource(symbolId, fromSnapshotId, toSnapshotId);
                changes.AddRange(sourceChanges);
            }

            
            var fromEdges = _store.GetEdges(fromSnapshotId);
            var toEdges = _store.GetEdges(toSnapshotId);

            var fromEdgeSet = new HashSet<(string source, string target, string kind)>(
                fromEdges.Select(e => (e.SourceSymbolId, e.TargetSymbolId, e.Kind)));
            var toEdgeSet = new HashSet<(string source, string target, string kind)>(
                toEdges.Select(e => (e.SourceSymbolId, e.TargetSymbolId, e.Kind)));

            
            foreach (var edge in toEdges)
            {
                var key = (edge.SourceSymbolId, edge.TargetSymbolId, edge.Kind);
                if (!fromEdgeSet.Contains(key))
                {
                    changes.Add(MakeChange(
                        fromSnapshotId, toSnapshotId,
                        ChangeType.EdgeAdded, edge.SourceSymbolId,
                        new { source = edge.SourceSymbolId, target = edge.TargetSymbolId, kind = edge.Kind }));
                }
            }

            
            foreach (var edge in fromEdges)
            {
                var key = (edge.SourceSymbolId, edge.TargetSymbolId, edge.Kind);
                if (!toEdgeSet.Contains(key))
                {
                    changes.Add(MakeChange(
                        fromSnapshotId, toSnapshotId,
                        ChangeType.EdgeRemoved, edge.SourceSymbolId,
                        new { source = edge.SourceSymbolId, target = edge.TargetSymbolId, kind = edge.Kind }));
                }
            }

            return changes;
        }

        
        
        

        private List<string> GetSymbolIdsInSnapshot(string snapshotId)
        {
            return _store.GetSymbolIdsInSnapshot(snapshotId);
        }

        private List<SemanticChange> CompareMetadata(
            string symbolId,
            string? fromJson,
            string? toJson,
            string fromSnapshotId,
            string toSnapshotId)
        {
            var changes = new List<SemanticChange>();

            var fromMeta = string.IsNullOrEmpty(fromJson)
                ? new Dictionary<string, JsonElement>()
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(fromJson) ?? new();

            var toMeta = string.IsNullOrEmpty(toJson)
                ? new Dictionary<string, JsonElement>()
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(toJson) ?? new();

            
            var fromAcc = GetMetaString(fromMeta, "accessibility");
            var toAcc = GetMetaString(toMeta, "accessibility");
            if (fromAcc != null && toAcc != null && fromAcc != toAcc)
            {
                changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.AccessibilityChanged, symbolId,
                    new { before = fromAcc, after = toAcc }));
            }

            
            var fromSig = GetMetaString(fromMeta, "signature");
            var toSig = GetMetaString(toMeta, "signature");
            if (fromSig != null && toSig != null && fromSig != toSig)
            {
                changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.SignatureChanged, symbolId,
                    new { before = fromSig, after = toSig }));
            }

            
            var fromBase = GetMetaString(fromMeta, "base_type");
            var toBase = GetMetaString(toMeta, "base_type");
            if (fromBase != null && toBase != null && fromBase != toBase)
            {
                changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.BaseTypeChanged, symbolId,
                    new { before = fromBase, after = toBase }));
            }

            
            var fromAttrs = GetMetaArray(fromMeta, "attributes");
            var toAttrs = GetMetaArray(toMeta, "attributes");
            if (fromAttrs != null && toAttrs != null && !fromAttrs.SequenceEqual(toAttrs))
            {
                changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.AttributeChanged, symbolId,
                    new { before = fromAttrs, after = toAttrs }));
            }

            return changes;
        }

        private List<SemanticChange> CompareSource(
            string symbolId,
            string fromSnapshotId,
            string toSnapshotId)
        {
            var changes = new List<SemanticChange>();

            var fromSig = _store.GetSymbolSource(symbolId, fromSnapshotId, ViewKind.Signature);
            var toSig = _store.GetSymbolSource(symbolId, toSnapshotId, ViewKind.Signature);

            if (fromSig == null || toSig == null)
                return changes;

            var fromBody = _store.GetSymbolSource(symbolId, fromSnapshotId, ViewKind.Body);
            var toBody = _store.GetSymbolSource(symbolId, toSnapshotId, ViewKind.Body);

            
            if (fromSig == toSig)
            {
                if (fromBody != toBody)
                {
                    changes.Add(MakeChange(fromSnapshotId, toSnapshotId, ChangeType.BodyOnlyChanged, symbolId,
                        new { note = "signature unchanged, body differs" }));
                }
            }

            return changes;
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

        private static SemanticChange MakeChange(
            string? fromSnapshotId,
            string? toSnapshotId,
            string changeType,
            string symbolId,
            object? detail)
        {
            return new SemanticChange(
                changeId: Guid.NewGuid().ToString("N"),
                fromSnapshotId: fromSnapshotId ?? string.Empty,
                toSnapshotId: toSnapshotId ?? string.Empty,
                changeType: changeType,
                symbolId: symbolId,
                detailJson: detail != null ? JsonSerializer.Serialize(detail) : null,
                createdAtUtc: DateTime.UtcNow);
        }
    }
}
