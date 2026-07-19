using Lurp.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

public sealed partial class MemberEdgeExtractor
{
    private List<EdgeRecord> ExtractCalls()
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

            var invocations = bodySyntax.DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(invocation);

                if (symbolInfo.Symbol is IMethodSymbol callee && callee.MethodKind != MethodKind.AnonymousFunction)
                {
                    var calleeId = MakeSymbolId(callee);

                    if (calleeId == null || calleeId == callerId)
                        continue;

                    var key = (callerId, calleeId, EdgeKind.Calls.ToString());

                    if (!seen.Add(key))
                        continue;

                    var loc = GetLocationInfo(invocation.GetLocation());

                    edges.Add(MakeEdge(callerId, calleeId, EdgeKind.Calls.ToString(), ExtractorConstants.CallsExtractor, loc));
                }
            }
        }

        return edges;
    }
}
