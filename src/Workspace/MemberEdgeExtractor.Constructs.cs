using Lurp.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

public sealed partial class MemberEdgeExtractor
{
    private List<EdgeRecord> ExtractConstructs()
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

            var creations = bodySyntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>();

            foreach (var creation in creations)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(creation);
                if (symbolInfo.Symbol is IMethodSymbol ctor && ctor.MethodKind == MethodKind.Constructor)
                {
                    var ctorId = MakeSymbolId(ctor);
                    if (ctorId == null)
                        continue;

                    var key = (callerId, ctorId, EdgeKind.Constructs.ToString());
                    if (!seen.Add(key))
                        continue;

                    var loc = GetLocationInfo(creation.GetLocation());
                    edges.Add(MakeEdge(callerId, ctorId, EdgeKind.Constructs.ToString(),
                        ExtractorConstants.ConstructsExtractor, loc));
                }
            }
        }

        return edges;
    }
}
