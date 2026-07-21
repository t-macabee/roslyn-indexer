using Microsoft.CodeAnalysis;
using Lurp.Storage;

namespace Lurp.Workspace;

public sealed class ReflectionExtractor
{
    private readonly ReflectionExtractionContext _context;

    public ReflectionExtractor(Compilation compilation, string snapshotId, string gitRoot)
    {
        if (compilation == null) throw new ArgumentNullException(nameof(compilation));
        if (snapshotId == null) throw new ArgumentNullException(nameof(snapshotId));
        _context = new ReflectionExtractionContext(compilation, snapshotId, gitRoot);
    }

    public List<EdgeRecord> Extract()
    {
        var edges = new List<EdgeRecord>();

        var typeOfExtractor = new TypeOfReflectionExtractor(_context);
        var nameOfExtractor = new NameOfReflectionExtractor(_context);
        var stringLiteralExtractor = new StringLiteralReflectionExtractor(_context);
        var unknownPatternExtractor = new UnknownPatternReflectionExtractor(_context);

        foreach (var syntaxTree in _context.Compilation.SyntaxTrees)
        {
            var semanticModel = _context.GetOrCreateSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();

            edges.AddRange(typeOfExtractor.Extract(root, semanticModel));
            edges.AddRange(nameOfExtractor.Extract(root, semanticModel));
            edges.AddRange(stringLiteralExtractor.Extract(root, semanticModel));
            edges.AddRange(unknownPatternExtractor.Extract(root, semanticModel));
        }

        return edges;
    }
}
