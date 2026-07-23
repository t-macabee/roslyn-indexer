using Lurp.Storage;
using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class ReturnsEdgeExtractor(MemberEdgeExtractionContext context) : IMemberEdgeExtractor
{
    List<EdgeRecord> IMemberEdgeExtractor.Extract()
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var typeSymbol in context.GetAllNamedTypes())
        {
            foreach (var member in typeSymbol.GetMembers())
            {
                if (member is not IMethodSymbol method)
                    continue;

                if (!context.IsMemberInScope(method))
                    continue;

                if (method.ReturnsVoid || method.ReturnType == null)
                    continue;

                var methodId = context.MakeSymbolId(method);
                var returnTypeId = context.MakeSymbolId(method.ReturnType);
                if (methodId == null || returnTypeId == null)
                    continue;

                var key = (methodId, returnTypeId, EdgeKind.Returns.ToString());
                if (!seen.Add(key))
                    continue;

                var loc = context.GetMemberSourceLocation(method);
                edges.Add(context.MakeEdge(methodId, returnTypeId, EdgeKind.Returns.ToString(),
                    ExtractorConstants.ReturnsExtractor, loc));
            }
        }

        return edges;
    }
}
