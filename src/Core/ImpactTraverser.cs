using System;
using System.Collections.Generic;
using System.Linq;
using Lurp.Storage;

namespace Lurp
{
    public sealed class ImpactTraverser
    {
        private readonly IIndexStore _store;
        private readonly string _snapshotId;

        public ImpactTraverser(IIndexStore store, string snapshotId)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _snapshotId = snapshotId ?? throw new ArgumentNullException(nameof(snapshotId));
        }

        public List<ImpactPath> TraceImpact(
            string symbolId,
            ImpactDirection direction,
            HashSet<string>? allowedEdgeKinds = null,
            int maxDepth = 10,
            bool includeSource = true)
        {
            var results = new List<ImpactPath>();

            var queue = new Queue<(string currentId, List<ImpactHop> hops, HashSet<string> visited)>();
            queue.Enqueue((symbolId, new List<ImpactHop>(), new HashSet<string> { symbolId }));

            while (queue.Count > 0)
            {
                var (currentId, hopsSoFar, visited) = queue.Dequeue();

                if (hopsSoFar.Count >= maxDepth)
                {
                    results.Add(new ImpactPath(
                        hops: hopsSoFar,
                        truncated: true,
                        truncationReason: "max depth reached"));
                    continue;
                }

                List<EdgeRecord> edges;
                try
                {
                    edges = direction switch
                    {
                        ImpactDirection.Downstream => _store.GetOutgoingEdges(_snapshotId, currentId),
                        ImpactDirection.Upstream => _store.GetIncomingEdges(_snapshotId, currentId),
                        _ => new List<EdgeRecord>()
                    };
                }
                catch
                {
                    continue;
                }

                if (edges.Count == 0 && hopsSoFar.Count > 0)
                {
                    results.Add(new ImpactPath(hops: hopsSoFar));
                    continue;
                }

                bool anyEdgeFollowed = false;

                foreach (var edge in edges)
                {
                    if (allowedEdgeKinds != null && !allowedEdgeKinds.Contains(edge.Kind))
                        continue;

                    string neighborId = direction switch
                    {
                        ImpactDirection.Downstream => edge.TargetSymbolId,
                        ImpactDirection.Upstream => edge.SourceSymbolId,
                        _ => throw new InvalidOperationException("Unknown impact direction")
                    };

                    if (visited.Contains(neighborId))
                        continue;

                    anyEdgeFollowed = true;

                    var newHop = new ImpactHop(
                        sourceSymbolId: edge.SourceSymbolId,
                        targetSymbolId: edge.TargetSymbolId,
                        edgeKind: edge.Kind,
                        provenance: edge.Provenance,
                        sourceDocument: includeSource ? edge.SourceDocumentPath : null,
                        sourceLine: includeSource ? edge.SourceStartLine : null);

                    var newHops = new List<ImpactHop>(hopsSoFar) { newHop };
                    var newVisited = new HashSet<string>(visited) { neighborId };

                    bool isLeaf = !HasRelevantEdges(neighborId, direction, allowedEdgeKinds);

                    if (isLeaf)
                    {
                        results.Add(new ImpactPath(hops: newHops));
                    }
                    else
                    {
                        queue.Enqueue((neighborId, newHops, newVisited));
                    }
                }

                if (!anyEdgeFollowed)
                {
                    if (hopsSoFar.Count > 0)
                    {
                        results.Add(new ImpactPath(hops: hopsSoFar));
                    }
                }
            }

            return results;
        }

        private bool HasRelevantEdges(
            string symbolId,
            ImpactDirection direction,
            HashSet<string>? allowedEdgeKinds)
        {
            try
            {
                var edges = direction switch
                {
                    ImpactDirection.Downstream => _store.GetOutgoingEdges(_snapshotId, symbolId),
                    ImpactDirection.Upstream => _store.GetIncomingEdges(_snapshotId, symbolId),
                    _ => new List<EdgeRecord>()
                };

                if (allowedEdgeKinds == null)
                    return edges.Count > 0;

                return edges.Any(e => allowedEdgeKinds.Contains(e.Kind));
            }
            catch
            {
                return false;
            }
        }
    }
}
