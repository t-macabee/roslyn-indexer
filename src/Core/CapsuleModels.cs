using System.Text.Json.Serialization;
using Lurp.Storage;

namespace Lurp
{
    public enum ContextIntent
    {
        Inspect,
        Modify,
        Diagnose
    }

    public sealed class CapsuleAnchor
    {
        [JsonPropertyName("symbolId")]
        public string SymbolId { get; init; }

        [JsonPropertyName("fullyQualifiedName")]
        public string FullyQualifiedName { get; init; }

        [JsonPropertyName("kind")]
        public string Kind { get; init; }

        [JsonPropertyName("source")]
        public string Source { get; init; }

        public CapsuleAnchor(string symbolId, string fullyQualifiedName, string kind, string source)
        {
            SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
            FullyQualifiedName = fullyQualifiedName ?? throw new ArgumentNullException(nameof(fullyQualifiedName));
            Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            Source = source ?? throw new ArgumentNullException(nameof(source));
        }
    }

    public sealed class CapsuleItem
    {
        [JsonPropertyName("symbolId")]
        public string SymbolId { get; init; }

        [JsonPropertyName("kind")]
        public string Kind { get; init; }

        [JsonPropertyName("fullyQualifiedName")]
        public string FullyQualifiedName { get; init; }

        [JsonPropertyName("provenance")]
        public string Provenance { get; init; }

        [JsonPropertyName("edgeKind")]
        public string EdgeKind { get; init; }

        [JsonPropertyName("source")]
        public string? Source { get; init; }

        public CapsuleItem(
            string symbolId,
            string kind,
            string fullyQualifiedName,
            string provenance,
            string edgeKind,
            string? source = null)
        {
            SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
            Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            FullyQualifiedName = fullyQualifiedName ?? throw new ArgumentNullException(nameof(fullyQualifiedName));
            Provenance = provenance ?? string.Empty;
            EdgeKind = edgeKind ?? throw new ArgumentNullException(nameof(edgeKind));
            Source = source;
        }
    }

    public sealed class UncertaintyEntry
    {
        [JsonPropertyName("symbolIds")]
        public List<string> SymbolIds { get; init; }

        [JsonPropertyName("relationshipKind")]
        public string RelationshipKind { get; init; }

        [JsonPropertyName("description")]
        public string Description { get; init; }

        public UncertaintyEntry(List<string> symbolIds, string relationshipKind, string description)
        {
            SymbolIds = symbolIds ?? throw new ArgumentNullException(nameof(symbolIds));
            RelationshipKind = relationshipKind ?? throw new ArgumentNullException(nameof(relationshipKind));
            Description = description ?? throw new ArgumentNullException(nameof(description));
        }
    }

    public sealed class VerificationSuggestion
    {
        [JsonPropertyName("testId")]
        public string TestId { get; init; }

        [JsonPropertyName("testName")]
        public string TestName { get; init; }

        [JsonPropertyName("description")]
        public string Description { get; init; }

        public VerificationSuggestion(string testId, string testName, string description)
        {
            TestId = testId ?? throw new ArgumentNullException(nameof(testId));
            TestName = testName ?? throw new ArgumentNullException(nameof(testName));
            Description = description ?? throw new ArgumentNullException(nameof(description));
        }
    }

    public sealed class ContextCapsule
    {
        [JsonPropertyName("anchor")]
        public CapsuleAnchor Anchor { get; init; }

        [JsonPropertyName("contracts")]
        public List<CapsuleItem> Contracts { get; init; } = new();

        [JsonPropertyName("directCallees")]
        public List<CapsuleItem> DirectCallees { get; init; } = new();

        [JsonPropertyName("directCallers")]
        public List<CapsuleItem> DirectCallers { get; init; } = new();

        [JsonPropertyName("registeredImplementations")]
        public List<CapsuleItem> RegisteredImplementations { get; init; } = new();

        [JsonPropertyName("relevantTests")]
        public List<CapsuleItem> RelevantTests { get; init; } = new();

        [JsonPropertyName("secondDegreeContext")]
        public List<CapsuleItem> SecondDegreeContext { get; init; } = new();

        [JsonPropertyName("budget")]
        public int Budget { get; init; }

        [JsonPropertyName("estimatedTokens")]
        public int EstimatedTokens { get; set; }

        [JsonPropertyName("truncated")]
        public bool Truncated { get; set; }

        [JsonPropertyName("truncatedCategories")]
        public List<string> TruncatedCategories { get; set; } = new();

        [JsonPropertyName("uncertainties")]
        public List<UncertaintyEntry> Uncertainties { get; init; } = new();

        [JsonPropertyName("suggestedVerification")]
        public List<VerificationSuggestion> SuggestedVerification { get; init; } = new();

        public ContextCapsule(CapsuleAnchor anchor)
        {
            Anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
        }
    }
}
