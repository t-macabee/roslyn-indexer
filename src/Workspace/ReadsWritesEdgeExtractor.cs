using Lurp.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class ReadsWritesEdgeExtractor(MemberEdgeExtractionContext context) : IMemberEdgeExtractor
{
    List<EdgeRecord> IMemberEdgeExtractor.Extract()
    {
        var edges = new List<EdgeRecord>();
        var seenReads = new HashSet<(string source, string target, string kind)>();
        var seenWrites = new HashSet<(string source, string target, string kind)>();
        var semanticModelCache = new Dictionary<SyntaxTree, SemanticModel>();

        foreach (var (methodSymbol, methodSyntax) in context.EnumerateMethodDeclarations())
        {
            var bodySyntax = MemberEdgeExtractionContext.GetMethodBody(methodSyntax);
            if (bodySyntax == null)
                continue;

            var semanticModel = context.GetOrCreateSemanticModel(methodSyntax.SyntaxTree, semanticModelCache);
            var callerId = context.MakeSymbolId(methodSymbol);
            if (callerId == null)
                continue;

            var accesses = bodySyntax.DescendantNodes()
                .Where(n => n is IdentifierNameSyntax or MemberAccessExpressionSyntax);

            foreach (var access in accesses)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(access);
                if (symbolInfo.Symbol is not IFieldSymbol and not IPropertySymbol)
                    continue;

                var memberSymbol = symbolInfo.Symbol;
                var memberId = context.MakeSymbolId(memberSymbol);
                if (memberId == null)
                    continue;

                bool isWrite = IsWriteContext(access);
                var kind = isWrite ? EdgeKind.Writes.ToString() : EdgeKind.Reads.ToString();
                var seenSet = isWrite ? seenWrites : seenReads;

                var key = (callerId, memberId, kind);
                if (!seenSet.Add(key))
                    continue;

                var loc = context.GetLocationInfo(access.GetLocation());
                edges.Add(context.MakeEdge(callerId, memberId, kind, ExtractorConstants.ReadsWritesExtractor, loc));
            }
        }

        return edges;
    }

    private static bool IsWriteContext(SyntaxNode node)
    {
        if (node.Parent is AssignmentExpressionSyntax assign)
            return assign.Left == node;

        if (node.Parent is PrefixUnaryExpressionSyntax preUnary && (preUnary.IsKind(SyntaxKind.PreIncrementExpression) ||
             preUnary.IsKind(SyntaxKind.PreDecrementExpression)))
        {
            return preUnary.Operand == node;
        }

        if (node.Parent is PostfixUnaryExpressionSyntax postUnary && (postUnary.IsKind(SyntaxKind.PostIncrementExpression) ||
             postUnary.IsKind(SyntaxKind.PostDecrementExpression)))
        {
            return postUnary.Operand == node;
        }

        if (node.Parent is ArgumentSyntax arg && (arg.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword) ||
             arg.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword)))
        {
            return true;
        }

        return false;
    }
}
