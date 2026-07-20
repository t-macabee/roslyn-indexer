using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class TypeOfReflectionExtractor(ReflectionExtractionContext context)
{
    internal List<EdgeRecord> Extract(SyntaxNode root, SemanticModel semanticModel)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var typeOfExpr in root.DescendantNodes().OfType<TypeOfExpressionSyntax>())
        {
            var typeInfo = semanticModel.GetTypeInfo(typeOfExpr.Type);
            if (typeInfo.Type == null)
                continue;

            var targetId = context.MakeSymbolId(typeInfo.Type);
            if (targetId == null)
                continue;

            var sourceId = context.GetContainingMemberSymbolId(typeOfExpr, semanticModel);
            if (sourceId == null)
                continue;

            var key = (sourceId, targetId, EdgeKind.ReflectionTypeRef.ToString());
            if (!seen.Add(key))
                continue;

            var loc = ReflectionExtractionContext.GetLocationInfo(typeOfExpr.GetLocation());
            edges.Add(new EdgeRecord
            {
                SourceSymbolId = sourceId,
                TargetSymbolId = targetId,
                Kind = EdgeKind.ReflectionTypeRef.ToString(),
                Provenance = "compiler_proved",
                SnapshotId = context.SnapshotId,
                ExtractorVersion = ExtractorConstants.ReflectionExtractor,
                SourceDocumentPath = loc.path,
                SourceStartLine = loc.startLine,
                SourceStartColumn = loc.startColumn,
                SourceEndLine = loc.endLine,
                SourceEndColumn = loc.endColumn,
            });
        }

        return edges;
    }
}
