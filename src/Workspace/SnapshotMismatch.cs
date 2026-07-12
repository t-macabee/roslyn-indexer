namespace RoslynIndexer;

/// <summary>
/// Classifies the kind of difference between two snapshots
/// (or between a snapshot and the live workspace).
/// </summary>
public enum MismatchKind
{
    /// <summary>A document exists in the new state but not in the baseline.</summary>
    DocumentAdded,

    /// <summary>A document exists in the baseline but not in the new state.</summary>
    DocumentRemoved,

    /// <summary>A document exists in both states but its content hash differs.</summary>
    DocumentModified,

    /// <summary>The .NET SDK version has changed.</summary>
    SdkChanged,

    /// <summary>The Roslyn compiler version has changed.</summary>
    CompilerChanged,

    /// <summary>The target framework of one or more projects has changed.</summary>
    TargetFrameworkChanged,

    /// <summary>A project exists in the new state but not in the baseline.</summary>
    ProjectAdded,

    /// <summary>A project exists in the baseline but not in the new state.</summary>
    ProjectRemoved,

    /// <summary>A project's set of project references has changed.</summary>
    ProjectReferenceChanged,

    /// <summary>Any indexer/extractor/schema version constant has changed.</summary>
    VersionChanged,
}

/// <summary>
/// Describes a single detected mismatch between two workspace states.
/// Every mismatch is traceable to a specific <see cref="Kind"/> and
/// carries a human-readable <see cref="Description"/>.
/// </summary>
/// <param name="Kind">The category of mismatch.</param>
/// <param name="Description">Human-readable explanation of what differs.</param>
/// <param name="Document">
/// The affected document, if the mismatch is document-scoped; <c>null</c> otherwise.
/// </param>
/// <param name="Detail">
/// Machine-oriented detail such as <c>"hash X → Y"</c> or <c>"net9.0 → net10.0"</c>;
/// <c>null</c> when no extra detail is needed.
/// </param>
public sealed record SnapshotMismatch(
    MismatchKind Kind,
    string Description,
    DocumentId? Document,
    string? Detail);
