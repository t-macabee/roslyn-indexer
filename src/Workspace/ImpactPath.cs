using System.Text.Json.Serialization;

namespace Lurp.Workspace
{
    public enum ImpactDirection
    {
        Downstream,
        Upstream
    }

    public sealed class ImpactHop
    {
        public string SourceSymbolId { get; }
        public string TargetSymbolId { get; }
        public string EdgeKind { get; }
        public string Provenance { get; }
        public string? SourceDocument { get; }
        public int? SourceLine { get; }

        [JsonConstructor]
        public ImpactHop(string sourceSymbolId,string targetSymbolId,string edgeKind,string provenance,string? sourceDocument = null,int? sourceLine = null)
        {
            SourceSymbolId = sourceSymbolId ?? throw new ArgumentNullException(nameof(sourceSymbolId));
            TargetSymbolId = targetSymbolId ?? throw new ArgumentNullException(nameof(targetSymbolId));
            EdgeKind = edgeKind ?? throw new ArgumentNullException(nameof(edgeKind));
            Provenance = provenance ?? string.Empty;
            SourceDocument = sourceDocument;
            SourceLine = sourceLine;
        }
    }

    public sealed class ImpactPath
    {
        public List<ImpactHop> Hops { get; }
        public bool Truncated { get; }
        public string? TruncationReason { get; }
        public int TotalSteps { get; }

        [JsonConstructor]
        public ImpactPath(List<ImpactHop> hops,bool truncated = false,string? truncationReason = null)
        {
            Hops = hops ?? throw new ArgumentNullException(nameof(hops));
            Truncated = truncated;
            TruncationReason = truncationReason;
            TotalSteps = hops.Count;
        }
    }
}
