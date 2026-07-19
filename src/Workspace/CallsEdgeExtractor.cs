using Lurp.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class CallsEdgeExtractor(MemberEdgeExtractionContext context) : IMemberEdgeExtractor
{
    List<EdgeRecord> IMemberEdgeExtractor.Extract()
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();
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

            var invocations = bodySyntax.DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(invocation);

                if (symbolInfo.Symbol is IMethodSymbol callee && callee.MethodKind != MethodKind.AnonymousFunction)
                {
                    var calleeId = context.MakeSymbolId(callee);

                    if (calleeId == null || calleeId == callerId)
                        continue;

                    var key = (callerId, calleeId, EdgeKind.Calls.ToString());

                    if (!seen.Add(key))
                        continue;

                    var loc = context.GetLocationInfo(invocation.GetLocation());

                    edges.Add(context.MakeEdge(callerId, calleeId, EdgeKind.Calls.ToString(), ExtractorConstants.CallsExtractor, loc));
                }
            }
        }

        return edges;
    }
}
