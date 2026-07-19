using System.Collections.Immutable;
using Lurp.Snapshots;
using Lurp.Storage;
namespace Lurp.Workspace;

public static class WorkspaceFreshness
{

    public sealed record FreshnessResult(bool IsFresh,IReadOnlyList<SnapshotMismatch> Mismatches,WorkspaceId CurrentWorkspaceId,SnapshotId? StoredSnapshotId,WorkspaceId? StoredWorkspaceId);

    public static FreshnessResult CheckFreshness(WorkspaceInfo current,ISnapshotStore store)
    {
        var storageManifest = store.LoadLatestSnapshot(current.Id.Value);

        if (storageManifest == null)
        {
            return new FreshnessResult(IsFresh: false,Mismatches: new List<SnapshotMismatch>{new(MismatchKind.VersionChanged,"Workspace has never been indexed — no snapshot manifest found.",Document: null,Detail: null)
                },
                CurrentWorkspaceId: current.Id,
                StoredSnapshotId: null,
                StoredWorkspaceId: null);
        }

        var richManifest = SnapshotManifest.FromStorageManifest(storageManifest);
        return CheckFreshness(current, richManifest);
    }

    public static FreshnessResult CheckFreshness(WorkspaceInfo current,SnapshotManifest? stored)
    {
        var mismatches = new List<SnapshotMismatch>();

        if (stored == null)
        {
            return new FreshnessResult(IsFresh: false,Mismatches: new List<SnapshotMismatch>{new(MismatchKind.VersionChanged,"Workspace has never been indexed — no snapshot manifest found.",Document: null,Detail: null)
                },
                CurrentWorkspaceId: current.Id,
                StoredSnapshotId: null,
                StoredWorkspaceId: null);
        }

        if (current.Id.Value != stored.WorkspaceId.Value)
        {
            mismatches.Add(new SnapshotMismatch(MismatchKind.SdkChanged,$"Workspace identity mismatch: current '{current.Id.Value}' vs stored '{stored.WorkspaceId.Value}'.",Document: null,Detail: $"{current.Id.Value} → {stored.WorkspaceId.Value}"));
        }

        var currentDocs = current.Documents;
        var storedDocs = stored.DocumentVersions;

        foreach (var (docId, _) in storedDocs)
        {
            if (!currentDocs.ContainsKey(docId))
            {
                mismatches.Add(new SnapshotMismatch(MismatchKind.DocumentRemoved,$"Document removed: '{docId}'.",Document: docId,Detail: null));
            }
        }

        foreach (var (docId, currentHash) in currentDocs)
        {
            if (!storedDocs.TryGetValue(docId, out var storedHash))
            {
                mismatches.Add(new SnapshotMismatch(MismatchKind.DocumentAdded,$"Document added: '{docId}'.",Document: docId,Detail: $"hash (new) = {currentHash}"));
            }
            else if (currentHash != storedHash)
            {
                mismatches.Add(new SnapshotMismatch(MismatchKind.DocumentModified,$"Document content changed: '{docId}'.",Document: docId,Detail: $"hash {storedHash} → {currentHash}"));
            }
        }

        if (!string.Equals(current.SdkVersion, stored.SdkVersion, StringComparison.Ordinal))
        {
            mismatches.Add(new SnapshotMismatch(MismatchKind.SdkChanged,$".NET SDK version changed.",Document: null,Detail: $"{stored.SdkVersion} → {current.SdkVersion}"));
        }

        var currentCompiler = current.CompilerVersion.ToString();
        if (!string.Equals(currentCompiler, stored.CompilerVersion, StringComparison.Ordinal))
        {
            mismatches.Add(new SnapshotMismatch(MismatchKind.CompilerChanged,"Roslyn compiler version changed.",Document: null,Detail: $"{stored.CompilerVersion} → {currentCompiler}"));
        }

        var currentTfms = current.TargetFrameworks;
        var storedTfms = stored.TargetFrameworks;

        foreach (var (projName, _) in storedTfms)
        {
            if (!currentTfms.ContainsKey(projName))
            {
                mismatches.Add(new SnapshotMismatch(MismatchKind.ProjectRemoved,$"Project removed: '{projName}'.",Document: null,Detail: projName));
            }
        }

        foreach (var (projName, currentTfm) in currentTfms)
        {
            if (!storedTfms.TryGetValue(projName, out var storedTfm))
            {
                mismatches.Add(new SnapshotMismatch(MismatchKind.ProjectAdded,$"Project added: '{projName}'.",Document: null,Detail: projName));
            }
            else if (!string.Equals(currentTfm, storedTfm, StringComparison.Ordinal))
            {
                mismatches.Add(new SnapshotMismatch(MismatchKind.TargetFrameworkChanged,$"Target framework changed for project '{projName}'.",Document: null,Detail: $"{storedTfm} → {currentTfm}"));
            }
        }

        var currentGraph = current.ProjectGraph;
        var storedGraph = stored.ProjectGraph;

        var allProjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in currentGraph.Keys) allProjects.Add(k);
        foreach (var k in storedGraph.Keys) allProjects.Add(k);

        foreach (var projName in allProjects)
        {
            var currentRefs = currentGraph.TryGetValue(projName, out var c)
                ? c : ImmutableHashSet<string>.Empty;
            var storedRefs = storedGraph.TryGetValue(projName, out var s)
                ? s : [];

            if (!currentRefs.SetEquals(storedRefs))
            {
                var currentSorted = currentRefs.OrderBy(x => x, StringComparer.Ordinal);
                var storedSorted = storedRefs.OrderBy(x => x, StringComparer.Ordinal);
                mismatches.Add(new SnapshotMismatch(MismatchKind.ProjectReferenceChanged,$"Project references changed for '{projName}'.",Document: null,Detail: $"stored=[{string.Join(", ", storedSorted)}]  current=[{string.Join(", ", currentSorted)}]"));
            }
        }

        if (!string.Equals(current.ExtractorVersion, stored.ExtractorVersion, StringComparison.Ordinal))
        {
            mismatches.Add(new SnapshotMismatch(MismatchKind.VersionChanged,"Extractor version changed.",Document: null,Detail: $"{stored.ExtractorVersion} → {current.ExtractorVersion}"));
        }

        return new FreshnessResult(IsFresh: mismatches.Count == 0,Mismatches: mismatches.AsReadOnly(),
            CurrentWorkspaceId: current.Id,
            StoredSnapshotId: stored.SnapshotId,
            StoredWorkspaceId: stored.WorkspaceId);
    }
}

