using System.Collections.Immutable;

namespace RoslynIndexer;

/// <summary>
/// Outcome of comparing a live <see cref="WorkspaceInfo"/> against a stored
/// <see cref="SnapshotManifest"/>.
/// </summary>
/// <param name="IsFresh">
/// <c>true</c> when zero mismatches were found — the index accurately reflects
/// the current workspace.
/// </param>
/// <param name="Mismatches">Every detected difference, one entry per root cause.</param>
/// <param name="CurrentWorkspaceId">The workspace identity of the live state.</param>
/// <param name="StoredSnapshotId">
/// The snapshot identity from the stored manifest, or <c>null</c> if no manifest existed.
/// </param>
/// <param name="StoredWorkspaceId">
/// The workspace identity recorded in the stored manifest, or <c>null</c> if none.
/// </param>
public sealed record FreshnessResult(
    bool IsFresh,
    IReadOnlyList<SnapshotMismatch> Mismatches,
    WorkspaceId CurrentWorkspaceId,
    SnapshotId? StoredSnapshotId,
    WorkspaceId? StoredWorkspaceId);

/// <summary>
/// Compares a live <see cref="WorkspaceInfo"/> with an optional stored
/// <see cref="SnapshotManifest"/> and reports everything that has changed.
/// </summary>
public static class WorkspaceFreshness
{
    /// <summary>
    /// Checks whether the current workspace state is fully reflected by
    /// the given stored manifest (or whether it has never been indexed).
    /// </summary>
    /// <param name="current">Live workspace information.</param>
    /// <param name="stored">
    /// Previously persisted snapshot, or <c>null</c> if none exists.
    /// </param>
    /// <returns>A <see cref="FreshnessResult"/> with the verdict and all mismatches.</returns>
    public static FreshnessResult CheckFreshness(
        WorkspaceInfo current,
        SnapshotManifest? stored)
    {
        var mismatches = new List<SnapshotMismatch>();

        if (stored == null)
        {
            return new FreshnessResult(
                IsFresh: false,
                Mismatches: new List<SnapshotMismatch>
                {
                    new(
                        MismatchKind.VersionChanged,
                        "Workspace has never been indexed — no snapshot manifest found.",
                        Document: null,
                        Detail: null)
                },
                CurrentWorkspaceId: current.Id,
                StoredSnapshotId: null,
                StoredWorkspaceId: null);
        }

        // ── 1. Workspace identity ───────────────────────────────────
        if (current.Id.Value != stored.WorkspaceId.Value)
        {
            mismatches.Add(new SnapshotMismatch(
                MismatchKind.SdkChanged, // closest semantic match for workspace mismatch
                $"Workspace identity mismatch: current '{current.Id.Value}' vs stored '{stored.WorkspaceId.Value}'.",
                Document: null,
                Detail: $"{current.Id.Value} → {stored.WorkspaceId.Value}"));
        }

        // ── 2. Document set & content hashes ────────────────────────
        var currentDocs = current.Documents;
        var storedDocs  = stored.DocumentVersions;

        // Documents removed (in stored but not in current)
        foreach (var (docId, _) in storedDocs)
        {
            if (!currentDocs.ContainsKey(docId))
            {
                mismatches.Add(new SnapshotMismatch(
                    MismatchKind.DocumentRemoved,
                    $"Document removed: '{docId}'.",
                    Document: docId,
                    Detail: null));
            }
        }

        // Documents added or modified (in current)
        foreach (var (docId, currentHash) in currentDocs)
        {
            if (!storedDocs.TryGetValue(docId, out var storedHash))
            {
                mismatches.Add(new SnapshotMismatch(
                    MismatchKind.DocumentAdded,
                    $"Document added: '{docId}'.",
                    Document: docId,
                    Detail: $"hash (new) = {currentHash}"));
            }
            else if (currentHash != storedHash)
            {
                mismatches.Add(new SnapshotMismatch(
                    MismatchKind.DocumentModified,
                    $"Document content changed: '{docId}'.",
                    Document: docId,
                    Detail: $"hash {storedHash} → {currentHash}"));
            }
        }

        // ── 3. SDK & compiler versions ──────────────────────────────
        if (!string.Equals(current.SdkVersion, stored.SdkVersion, StringComparison.Ordinal))
        {
            mismatches.Add(new SnapshotMismatch(
                MismatchKind.SdkChanged,
                $".NET SDK version changed.",
                Document: null,
                Detail: $"{stored.SdkVersion} → {current.SdkVersion}"));
        }

        var currentCompiler = current.CompilerVersion.ToString();
        if (!string.Equals(currentCompiler, stored.CompilerVersion, StringComparison.Ordinal))
        {
            mismatches.Add(new SnapshotMismatch(
                MismatchKind.CompilerChanged,
                "Roslyn compiler version changed.",
                Document: null,
                Detail: $"{stored.CompilerVersion} → {currentCompiler}"));
        }

        // ── 4. Project set & target frameworks ──────────────────────
        var currentTfms = current.TargetFrameworks;
        var storedTfms  = stored.TargetFrameworks;

        // Projects removed
        foreach (var (projName, _) in storedTfms)
        {
            if (!currentTfms.ContainsKey(projName))
            {
                mismatches.Add(new SnapshotMismatch(
                    MismatchKind.ProjectRemoved,
                    $"Project removed: '{projName}'.",
                    Document: null,
                    Detail: projName));
            }
        }

        // Projects added or TFM changed
        foreach (var (projName, currentTfm) in currentTfms)
        {
            if (!storedTfms.TryGetValue(projName, out var storedTfm))
            {
                mismatches.Add(new SnapshotMismatch(
                    MismatchKind.ProjectAdded,
                    $"Project added: '{projName}'.",
                    Document: null,
                    Detail: projName));
            }
            else if (!string.Equals(currentTfm, storedTfm, StringComparison.Ordinal))
            {
                mismatches.Add(new SnapshotMismatch(
                    MismatchKind.TargetFrameworkChanged,
                    $"Target framework changed for project '{projName}'.",
                    Document: null,
                    Detail: $"{storedTfm} → {currentTfm}"));
            }
        }

        // ── 5. Project reference graph ──────────────────────────────
        var currentGraph = current.ProjectGraph;
        var storedGraph  = stored.ProjectGraph;

        // Union of all project names across both states
        var allProjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in currentGraph.Keys) allProjects.Add(k);
        foreach (var k in storedGraph.Keys)  allProjects.Add(k);

        foreach (var projName in allProjects)
        {
            var currentRefs = currentGraph.TryGetValue(projName, out var c)
                ? c : ImmutableHashSet<string>.Empty;
            var storedRefs = storedGraph.TryGetValue(projName, out var s)
                ? s : [];

            if (!currentRefs.SetEquals(storedRefs))
            {
                var currentSorted = currentRefs.OrderBy(x => x, StringComparer.Ordinal);
                var storedSorted  = storedRefs.OrderBy(x => x, StringComparer.Ordinal);
                mismatches.Add(new SnapshotMismatch(
                    MismatchKind.ProjectReferenceChanged,
                    $"Project references changed for '{projName}'.",
                    Document: null,
                    Detail: $"stored=[{string.Join(", ", storedSorted)}]  current=[{string.Join(", ", currentSorted)}]"));
            }
        }

        // ── 6. Extractor version pin ────────────────────────────────
        if (!string.Equals(current.ExtractorVersion, stored.ExtractorVersion, StringComparison.Ordinal))
        {
            mismatches.Add(new SnapshotMismatch(
                MismatchKind.VersionChanged,
                "Extractor version changed.",
                Document: null,
                Detail: $"{stored.ExtractorVersion} → {current.ExtractorVersion}"));
        }

        return new FreshnessResult(
            IsFresh: mismatches.Count == 0,
            Mismatches: mismatches.AsReadOnly(),
            CurrentWorkspaceId: current.Id,
            StoredSnapshotId: stored.SnapshotId,
            StoredWorkspaceId: stored.WorkspaceId);
    }
}
