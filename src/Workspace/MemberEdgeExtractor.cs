using Lurp.Storage;
using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

public sealed class MemberEdgeExtractor
{
    private readonly List<IMemberEdgeExtractor> _extractors;

    public MemberEdgeExtractor(Compilation compilation, IReadOnlyDictionary<DocumentId, DocumentVersionId> documentVersions, IReadOnlySet<DocumentId> generatedDocuments, string snapshotId, string gitRoot)
    {
        var context = new MemberEdgeExtractionContext(compilation, documentVersions, generatedDocuments, snapshotId, gitRoot);

        _extractors =
        [
            new DeclaresEdgeExtractor(context),
            new CallsEdgeExtractor(context),
            new ConstructsEdgeExtractor(context),
            new OverridesEdgeExtractor(context),
            new ReadsWritesEdgeExtractor(context),
            new ReturnsEdgeExtractor(context),
            new ParameterDependencyEdgeExtractor(context),
            new ThrowsEdgeExtractor(context),
        ];
    }

    public List<EdgeRecord> ExtractAll()
    {
        var allEdges = new List<EdgeRecord>();

        foreach (var extractor in _extractors)
            allEdges.AddRange(extractor.Extract());

        return allEdges;
    }
}
