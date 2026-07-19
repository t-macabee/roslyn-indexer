using Lurp.Storage;
using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

public sealed partial class MemberEdgeExtractor
{
    private List<EdgeRecord> ExtractParameterDependencies()
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var typeSymbol in GetNamespaceTypeMembers(_compilation.Assembly.GlobalNamespace))
        {
            foreach (var member in typeSymbol.GetMembers())
            {
                if (member is not IMethodSymbol method)
                    continue;

                var methodId = MakeSymbolId(method);
                if (methodId == null)
                    continue;

                foreach (var param in method.Parameters)
                {
                    if (param.Type == null)
                        continue;

                    var paramTypeId = MakeSymbolId(param.Type);
                    if (paramTypeId == null)
                        continue;

                    var key = (methodId, paramTypeId, EdgeKind.References.ToString());
                    if (!seen.Add(key))
                        continue;

                    var loc = GetMemberSourceLocation(method);
                    edges.Add(MakeEdge(methodId, paramTypeId, EdgeKind.References.ToString(),
                        ExtractorConstants.ParameterDependenciesExtractor, loc));
                }
            }
        }

        return edges;
    }
}
