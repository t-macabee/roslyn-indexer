using Lurp.Storage;
using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

public sealed partial class MemberEdgeExtractor
{
    private List<EdgeRecord> ExtractReturns()
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var typeSymbol in GetNamespaceTypeMembers(_compilation.Assembly.GlobalNamespace))
        {
            foreach (var member in typeSymbol.GetMembers())
            {
                if (member is not IMethodSymbol method)
                    continue;

                if (method.ReturnsVoid || method.ReturnType == null)
                    continue;

                var methodId = MakeSymbolId(method);
                var returnTypeId = MakeSymbolId(method.ReturnType);
                if (methodId == null || returnTypeId == null)
                    continue;

                var key = (methodId, returnTypeId, EdgeKind.Returns.ToString());
                if (!seen.Add(key))
                    continue;

                var loc = GetMemberSourceLocation(method);
                edges.Add(MakeEdge(methodId, returnTypeId, EdgeKind.Returns.ToString(),
                    ExtractorConstants.ReturnsExtractor, loc));
            }
        }

        return edges;
    }
}
