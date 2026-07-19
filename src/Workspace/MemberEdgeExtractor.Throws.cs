using Lurp.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

public sealed partial class MemberEdgeExtractor
{
    private List<EdgeRecord> ExtractThrows()
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();
        var semanticModelCache = new Dictionary<SyntaxTree, SemanticModel>();

        foreach (var (methodSymbol, methodSyntax) in EnumerateMethodDeclarations())
        {
            var bodySyntax = GetMethodBody(methodSyntax);
            if (bodySyntax == null)
                continue;

            var semanticModel = GetOrCreateSemanticModel(methodSyntax.SyntaxTree, semanticModelCache);
            var callerId = MakeSymbolId(methodSymbol);
            if (callerId == null)
                continue;

            foreach (var throwStmt in bodySyntax.DescendantNodes().OfType<ThrowStatementSyntax>())
            {
                if (throwStmt.Expression == null)
                    continue;

                var exceptionType = ResolveThrownType(throwStmt.Expression, semanticModel);
                if (exceptionType == null)
                    continue;

                var typeId = MakeSymbolId(exceptionType);
                if (typeId == null)
                    continue;

                var key = (callerId, typeId, EdgeKind.Throws.ToString());
                if (!seen.Add(key))
                    continue;

                var loc = GetLocationInfo(throwStmt.GetLocation());
                edges.Add(MakeEdge(callerId, typeId, EdgeKind.Throws.ToString(),
                    ExtractorConstants.ThrowsExtractor, loc));
            }

            foreach (var throwExpr in bodySyntax.DescendantNodes().OfType<ThrowExpressionSyntax>())
            {
                if (throwExpr.Expression == null)
                    continue;

                var exceptionType = ResolveThrownType(throwExpr.Expression, semanticModel);
                if (exceptionType == null)
                    continue;

                var typeId = MakeSymbolId(exceptionType);
                if (typeId == null)
                    continue;

                var key = (callerId, typeId, EdgeKind.Throws.ToString());
                if (!seen.Add(key))
                    continue;

                var loc = GetLocationInfo(throwExpr.GetLocation());
                edges.Add(MakeEdge(callerId, typeId, EdgeKind.Throws.ToString(),
                    ExtractorConstants.ThrowsExtractor, loc));
            }
        }

        return edges;
    }

    private static INamedTypeSymbol? ResolveThrownType(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        if (expression is ObjectCreationExpressionSyntax creation)
        {
            var symbolInfo = semanticModel.GetSymbolInfo(creation);
            if (symbolInfo.Symbol is IMethodSymbol ctor)
                return ctor.ContainingType;
        }

        var typeInfo = semanticModel.GetTypeInfo(expression);
        return typeInfo.Type as INamedTypeSymbol;
    }
}
