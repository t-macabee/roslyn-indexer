using Microsoft.CodeAnalysis;
using Lurp.Storage;

namespace Lurp.Workspace;

/// <summary>
/// Extracts polymorphism-related edges:
///
///   1. may_dispatch_to  — from an interface member (or virtual root) to its
///      concrete implementations / overrides.
///   2. statically_calls — from a calling method to the interface/abstract/virtual
///      member it invokes (the "dispatch point").
///
/// Provenance on may_dispatch_to:
///   - "compiler_proved"   — the implementing member is directly declared on the
///                            type (not inherited through a base), or the override
///                            is reachable through a known virtual-chain root.
///   - "possible"          — the implementation is inherited from a base type;
///                            it is a valid dispatch target but a future re-implementation
///                            in a derived type could shadow it.
///   - "framework_derived" — (NOT emitted here) emitted by DependencyInjectionAdapter
///                            as a separate may_dispatch_to edge when DI registration
///                            evidence narrows the candidate set.  See the adapter
///                            for details.
///
/// Note: MemberEdgeExtractor already emits Calls edges for *all* invocations
/// (including dispatch-point calls).  The statically_calls edge is an additional
/// marker that identifies *which* call sites target a polymorphic dispatch point,
/// enabling graph traversal to follow the call → interface member → implementation
/// chain.
/// </summary>
public sealed class PolymorphismExtractor
{
    private readonly PolymorphismExtractionContext _context;

    public PolymorphismExtractor(Compilation compilation, string snapshotId)
    {
        if (compilation == null) throw new ArgumentNullException(nameof(compilation));
        if (snapshotId == null) throw new ArgumentNullException(nameof(snapshotId));
        _context = new PolymorphismExtractionContext(compilation, snapshotId);
    }

    public List<EdgeRecord> ExtractAll()
    {
        var allTypes = PolymorphismExtractionContext.GetAllNamedTypes(_context.Compilation.Assembly.GlobalNamespace);

        var edges = new List<EdgeRecord>();
        edges.AddRange(new InterfaceDispatchExtractor(_context).Extract(allTypes));
        edges.AddRange(new VirtualOverrideExtractor(_context).Extract(allTypes));
        edges.AddRange(new StaticDispatchCallExtractor(_context).Extract(allTypes));
        return edges;
    }
}
