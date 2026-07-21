using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class NameOfReflectionExtractor(ReflectionExtractionContext context)
{
    internal List<EdgeRecord> Extract(SyntaxNode root, SemanticModel semanticModel)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!IsNameOfInvocation(invocation))
                continue;

            EmitNameOfEdge(invocation, semanticModel, edges, seen);
        }

        return edges;
    }

    private static bool IsNameOfInvocation(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression is IdentifierNameSyntax identifier
            && identifier.Identifier.Text.Equals("nameof", StringComparison.Ordinal)
            && invocation.ArgumentList.Arguments.Count == 1;
    }

    private void EmitNameOfEdge(InvocationExpressionSyntax invocation, SemanticModel semanticModel, List<EdgeRecord> edges, HashSet<(string source, string target, string kind)> seen)
    {
        var sourceId = context.GetContainingMemberSymbolId(invocation, semanticModel);
        if (sourceId == null)
            return;

        var argument = invocation.ArgumentList.Arguments[0].Expression;
        var symbolInfo = semanticModel.GetSymbolInfo(argument);
        var resolvedSymbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();

        if (resolvedSymbol == null || !resolvedSymbol.CanBeReferencedByName)
            return;

        var targetId = context.MakeSymbolId(resolvedSymbol);
        if (targetId == null)
            return;

        var key = (sourceId, targetId, EdgeKind.ReflectionMemberRef.ToString());
        if (!seen.Add(key))
            return;

        var loc = context.GetLocationInfo(invocation.GetLocation());
        edges.Add(new EdgeRecord
        {
            SourceSymbolId = sourceId,
            TargetSymbolId = targetId,
            Kind = EdgeKind.ReflectionMemberRef.ToString(),
            Provenance = Provenance.CompilerProved,
            SnapshotId = context.SnapshotId,
            ExtractorVersion = ExtractorConstants.ReflectionExtractor,
            SourceDocumentPath = loc.path,
            SourceStartLine = loc.startLine,
            SourceStartColumn = loc.startColumn,
            SourceEndLine = loc.endLine,
            SourceEndColumn = loc.endColumn,
        });
    }
}
