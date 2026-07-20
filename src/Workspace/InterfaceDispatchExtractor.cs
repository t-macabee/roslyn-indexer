using Microsoft.CodeAnalysis;
using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

/// <summary>
/// For every type that implements an interface, emit a may_dispatch_to edge
/// from each interface member to the effective implementation.
///
/// Dispatch resolution strategy:
///   For each concrete type, iterate over AllInterfaces and their members.
///   Use Roslyn's FindImplementationForInterfaceMember to resolve the
///   effective implementation (which may be inherited from a base type).
///   Then classify provenance: "compiler_proved" if the implementing member
///   is declared directly on the type itself, "possible" if inherited.
/// </summary>
internal sealed class InterfaceDispatchExtractor(PolymorphismExtractionContext context)
{
    internal List<EdgeRecord> Extract(List<INamedTypeSymbol> allTypes)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var type in allTypes)
        {
            if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct)
                continue;

            if (type.AllInterfaces.IsEmpty)
                continue;

            foreach (var iface in type.AllInterfaces)
            {
                foreach (var member in iface.GetMembers())
                {
                    TryEmitInterfaceDispatchEdge(type, member, edges, seen);
                }
            }
        }

        return edges;
    }

    private void TryEmitInterfaceDispatchEdge(INamedTypeSymbol type, ISymbol member, List<EdgeRecord> edges, HashSet<(string source, string target, string kind)> seen)
    {
        if (member is not IMethodSymbol and not IPropertySymbol and not IEventSymbol)
            return;

        var ifaceMemberId = context.MakeSymbolId(member);
        if (ifaceMemberId == null)
            return;

        var implMember = type.FindImplementationForInterfaceMember(member);
        if (implMember == null)
            return;

        var implMemberId = context.MakeSymbolId(implMember);
        if (implMemberId == null || implMemberId == ifaceMemberId)
            return;

        var key = (ifaceMemberId, implMemberId, EdgeKind.MayDispatchTo.ToString());
        if (!seen.Add(key))
            return;

        // If the implementing member is declared on *this* type directly
        // (rather than inherited from a base), it is compiler-proved.
        bool isDirect = SymbolEqualityComparer.Default.Equals(implMember.ContainingType, type);
        string provenance = isDirect ? "compiler_proved" : "possible";

        edges.Add(context.MakeMayDispatchEdge(ifaceMemberId, implMemberId, implMember, provenance));
    }
}
