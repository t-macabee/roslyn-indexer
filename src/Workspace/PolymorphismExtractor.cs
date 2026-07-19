using Lurp.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;
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
    private readonly Compilation _compilation;
    private readonly string _snapshotId;
    private readonly string _assemblyIdentity;

    public PolymorphismExtractor(Compilation compilation,string snapshotId)
    {
        _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        _snapshotId = snapshotId ?? throw new ArgumentNullException(nameof(snapshotId));
        _assemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
    }

    public List<EdgeRecord> ExtractAll()
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();
        var allTypes = GetAllNamedTypes(_compilation.Assembly.GlobalNamespace);

        ExtractInterfaceDispatches(allTypes, edges, seen);
        ExtractVirtualOverrides(allTypes, edges, seen);
        ExtractStaticCalls(allTypes, edges, seen);

        return edges;
    }

    // ----------------------------------------------------------------
    //  may_dispatch_to: interface member → implementing member
    // ----------------------------------------------------------------

    /// <summary>
    /// For every type that implements an interface, emit a may_dispatch_to edge
    /// from each interface member to the effective implementation.
    ///
    /// Dispatch resolution strategy:
    ///   For each concrete type, iterate over AllInterfaces and their members.
    ///   Use Roslyn's FindImplementationForInterfaceMember to resolve the
    ///   effective implementation (which may be inherited from a base type).
    ///   Then classify provenance: "compiler_proved" if the implementing member
    ///   is declared directly on the type itself, "possible" if inherited
    ///   (still a valid target, but a derived type could re-implement the
    ///   interface in a future compilation, changing the dispatch).
    ///
    /// Provenance:
    ///   "compiler_proved" when the implementing member is declared directly on
    ///   the type (not inherited from a base type).
    ///   "possible" when inherited (still a valid target, but a derived type
    ///   could re-implement the interface in a future compilation).
    /// </summary>
    private void ExtractInterfaceDispatches(List<INamedTypeSymbol> allTypes,List<EdgeRecord> edges,HashSet<(string source, string target, string kind)> seen)
    {
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
                    if (member is not IMethodSymbol and not IPropertySymbol and not IEventSymbol)
                        continue;

                    var ifaceMemberId = MakeSymbolId(member);
                    if (ifaceMemberId == null)
                        continue;

                    var implMember = type.FindImplementationForInterfaceMember(member);
                    if (implMember == null)
                        continue;

                    var implMemberId = MakeSymbolId(implMember);
                    if (implMemberId == null || implMemberId == ifaceMemberId)
                        continue;

                    var key = (ifaceMemberId, implMemberId, EdgeKind.MayDispatchTo.ToString());
                    if (!seen.Add(key))
                        continue;

                    // If the implementing member is declared on *this* type directly
                    // (rather than inherited from a base), it is compiler-proved.
                    bool isDirect = SymbolEqualityComparer.Default.Equals(implMember.ContainingType, type);
                    string provenance = isDirect ? "compiler_proved" : "possible";

                    edges.Add(new EdgeRecord
                    {
                        SourceSymbolId = ifaceMemberId,
                        TargetSymbolId = implMemberId,
                        Kind = EdgeKind.MayDispatchTo.ToString(),
                        Provenance = provenance,
                        SnapshotId = _snapshotId,
                        ExtractorVersion = ExtractorConstants.PolymorphismExtractor,
                        SourceDocumentPath = GetDocumentPath(implMember),
                        SourceStartLine = GetStartLine(implMember),
                        SourceStartColumn = GetStartColumn(implMember),
                        SourceEndLine = GetEndLine(implMember),
                        SourceEndColumn = GetEndColumn(implMember),
                    });
                }
            }
        }
    }

    // ----------------------------------------------------------------
    //  may_dispatch_to: virtual root → override
    // ----------------------------------------------------------------

    /// <summary>
    /// For every override, walk to the root virtual declaration and emit a
    /// may_dispatch_to edge from the root to the override.  Because the
    /// override chain is fully resolved at compile time within a single
    /// compilation, all such edges are "compiler_proved".
    /// </summary>
    private void ExtractVirtualOverrides(List<INamedTypeSymbol> allTypes,List<EdgeRecord> edges,HashSet<(string source, string target, string kind)> seen)
    {
        foreach (var type in allTypes)
        {
            foreach (var member in type.GetMembers())
            {
                if (member is IMethodSymbol method && method.IsOverride && method.OverriddenMethod != null)
                {
                    var root = WalkToRootOverride(method);
                    var rootId = MakeSymbolId(root);
                    var overrideId = MakeSymbolId(method);
                    if (rootId == null || overrideId == null || rootId == overrideId)
                        continue;

                    var key = (rootId, overrideId, EdgeKind.MayDispatchTo.ToString());
                    if (!seen.Add(key))
                        continue;

                    edges.Add(MakeMayDispatchEdge(rootId, overrideId, method, "compiler_proved"));
                }

                if (member is IPropertySymbol prop && prop.IsOverride && prop.OverriddenProperty != null)
                {
                    var root = WalkToRootOverride(prop);
                    var rootId = MakeSymbolId(root);
                    var overrideId = MakeSymbolId(prop);
                    if (rootId == null || overrideId == null || rootId == overrideId)
                        continue;

                    var key = (rootId, overrideId, EdgeKind.MayDispatchTo.ToString());
                    if (!seen.Add(key))
                        continue;

                    edges.Add(MakeMayDispatchEdge(rootId, overrideId, prop, "compiler_proved"));
                }

                if (member is IEventSymbol evt && evt.IsOverride && evt.OverriddenEvent != null)
                {
                    var root = WalkToRootOverride(evt);
                    var rootId = MakeSymbolId(root);
                    var overrideId = MakeSymbolId(evt);
                    if (rootId == null || overrideId == null || rootId == overrideId)
                        continue;

                    var key = (rootId, overrideId, EdgeKind.MayDispatchTo.ToString());
                    if (!seen.Add(key))
                        continue;

                    edges.Add(MakeMayDispatchEdge(rootId, overrideId, evt, "compiler_proved"));
                }
            }
        }
    }

    // ----------------------------------------------------------------
    //  statically_calls: caller → dispatch-point member
    // ----------------------------------------------------------------

    /// <summary>
    /// Walk every method body in the compilation and emit a StaticallyCalls
    /// edge for each invocation whose target is a polymorphic dispatch point:
    /// an interface member, an abstract member, or a virtual (non-sealed) member.
    ///
    /// These edges are complementary to the Calls edges emitted by
    /// MemberEdgeExtractor — Calls covers every invocation (concrete + dispatch),
    /// while this edge explicitly marks the dispatch-point calls so that the
    /// graph can be traversed as:
    ///
    ///   Handler.Handle --[statically_calls]--> IRepository.SaveAsync
    ///   IRepository.SaveAsync --[may_dispatch_to]--> Repository.SaveAsync
    /// </summary>
    private void ExtractStaticCalls(List<INamedTypeSymbol> allTypes,List<EdgeRecord> edges,HashSet<(string source, string target, string kind)> seen)
    {
        var semanticModelCache = new Dictionary<SyntaxTree, SemanticModel>();

        foreach (var (methodSymbol, methodSyntax) in EnumerateMethodDeclarations(allTypes))
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
                    // A "dispatch point" is a method whose runtime target requires
                    // polymorphic resolution: interface methods, abstract methods,
                    // and virtual (non-sealed) methods.
                    bool isDispatchPoint = callee.ContainingType?.TypeKind == TypeKind.Interface
                        || callee.IsAbstract
                        || (callee.IsVirtual && !callee.IsSealed);

                    if (!isDispatchPoint)
                        continue;

                    var calleeId = MakeSymbolId(callee);
                    if (calleeId == null || calleeId == callerId)
                        continue;

                    var key = (callerId, calleeId, EdgeKind.StaticallyCalls.ToString());
                    if (!seen.Add(key))
                        continue;

                    var loc = GetLocationInfo(invocation.GetLocation());
                    edges.Add(new EdgeRecord
                    {
                        SourceSymbolId = callerId,
                        TargetSymbolId = calleeId,
                        Kind = EdgeKind.StaticallyCalls.ToString(),
                        Provenance = "compiler_proved",
                        SnapshotId = _snapshotId,
                        ExtractorVersion = ExtractorConstants.StaticallyCallsExtractor,
                        SourceDocumentPath = loc.path,
                        SourceStartLine = loc.startLine,
                        SourceStartColumn = loc.startColumn,
                        SourceEndLine = loc.endLine,
                        SourceEndColumn = loc.endColumn,
                    });
                }
            }
        }
    }

    // ----------------------------------------------------------------
    //  Helpers — method body enumeration
    // ----------------------------------------------------------------

    /// <summary>
    /// Enumerate all method-like declarations (methods, constructors, accessors)
    /// owned by types in the current compilation, paired with their syntax nodes.
    /// </summary>
    private static IEnumerable<(IMethodSymbol method, CSharpSyntaxNode syntax)> EnumerateMethodDeclarations(List<INamedTypeSymbol> allTypes)
    {
        foreach (var typeSymbol in allTypes)
        {
            foreach (var member in typeSymbol.GetMembers())
            {
                if (member is IMethodSymbol method)
                {
                    foreach (var syntaxRef in method.DeclaringSyntaxReferences)
                    {
                        var syntax = syntaxRef.GetSyntax();
                        if (syntax is MethodDeclarationSyntax methodSyntax)
                            yield return (method, methodSyntax);
                        else if (syntax is ConstructorDeclarationSyntax ctorSyntax)
                            yield return (method, ctorSyntax);
                    }
                }

                if (member is IPropertySymbol property)
                {
                    foreach (var accessor in new[] { property.GetMethod, property.SetMethod })
                    {
                        if (accessor == null)
                            continue;

                        foreach (var syntaxRef in accessor.DeclaringSyntaxReferences)
                        {
                            if (syntaxRef.GetSyntax() is AccessorDeclarationSyntax accessorSyntax)
                                yield return (accessor, accessorSyntax);
                        }
                    }
                }
            }
        }
    }

    private static SyntaxNode? GetMethodBody(CSharpSyntaxNode node)
    {
        return node switch
        {
            MethodDeclarationSyntax m => m.Body ?? (SyntaxNode?)m.ExpressionBody,
            ConstructorDeclarationSyntax c => c.Body ?? (SyntaxNode?)c.ExpressionBody,
            AccessorDeclarationSyntax a => a.Body ?? (SyntaxNode?)a.ExpressionBody,
            _ => null
        };
    }

    private SemanticModel GetOrCreateSemanticModel(SyntaxTree syntaxTree,Dictionary<SyntaxTree, SemanticModel> cache)
    {
        if (!cache.TryGetValue(syntaxTree, out var model))
        {
            model = _compilation.GetSemanticModel(syntaxTree);
            cache[syntaxTree] = model;
        }
        return model;
    }

    // ----------------------------------------------------------------
    //  Helpers — walk override chains
    // ----------------------------------------------------------------

    private static IMethodSymbol WalkToRootOverride(IMethodSymbol method)
    {
        var current = method;
        while (current.IsOverride && current.OverriddenMethod != null)
            current = current.OverriddenMethod;
        return current;
    }

    private static IPropertySymbol WalkToRootOverride(IPropertySymbol prop)
    {
        var current = prop;
        while (current.IsOverride && current.OverriddenProperty != null)
            current = current.OverriddenProperty;
        return current;
    }

    private static IEventSymbol WalkToRootOverride(IEventSymbol evt)
    {
        var current = evt;
        while (current.IsOverride && current.OverriddenEvent != null)
            current = current.OverriddenEvent;
        return current;
    }

    // ----------------------------------------------------------------
    //  Helpers — edge construction
    // ----------------------------------------------------------------

    private EdgeRecord MakeMayDispatchEdge(string sourceId, string targetId, ISymbol targetSymbol, string provenance)
    {
        return new EdgeRecord
        {
            SourceSymbolId = sourceId,
            TargetSymbolId = targetId,
            Kind = EdgeKind.MayDispatchTo.ToString(),
            Provenance = provenance,
            SnapshotId = _snapshotId,
            ExtractorVersion = ExtractorConstants.PolymorphismExtractor,
            SourceDocumentPath = GetDocumentPath(targetSymbol),
            SourceStartLine = GetStartLine(targetSymbol),
            SourceStartColumn = GetStartColumn(targetSymbol),
            SourceEndLine = GetEndLine(targetSymbol),
            SourceEndColumn = GetEndColumn(targetSymbol),
        };
    }

    // ----------------------------------------------------------------
    //  Helpers — symbol identity & location
    // ----------------------------------------------------------------

    private string? MakeSymbolId(ISymbol symbol)
    {
        var docCommentId = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(docCommentId))
            return null;
        return $"{docCommentId}|{_assemblyIdentity}";
    }

    private static string? GetDocumentPath(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var path = syntaxRef.SyntaxTree?.FilePath;
        return string.IsNullOrEmpty(path) ? null : path.Replace('\\', '/');
    }

    private static int? GetStartLine(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var span = syntaxRef.GetSyntax().GetLocation().GetLineSpan();
        return span.StartLinePosition.Line;
    }

    private static int? GetStartColumn(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var span = syntaxRef.GetSyntax().GetLocation().GetLineSpan();
        return span.StartLinePosition.Character;
    }

    private static int? GetEndLine(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var span = syntaxRef.GetSyntax().GetLocation().GetLineSpan();
        return span.EndLinePosition.Line;
    }

    private static int? GetEndColumn(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var span = syntaxRef.GetSyntax().GetLocation().GetLineSpan();
        return span.EndLinePosition.Character;
    }

    private (string? path, int? startLine, int? startColumn, int? endLine, int? endColumn)
        GetLocationInfo(Location location)
    {
        if (location == null || !location.IsInSource)
            return (null, null, null, null, null);

        var lineSpan = location.GetLineSpan();
        return (location.SourceTree?.FilePath?.Replace('\\', '/'),
                lineSpan.StartLinePosition.Line,
                lineSpan.StartLinePosition.Character,
                lineSpan.EndLinePosition.Line,
                lineSpan.EndLinePosition.Character);
    }

    // ----------------------------------------------------------------
    //  Helpers — type enumeration
    // ----------------------------------------------------------------

    private static List<INamedTypeSymbol> GetAllNamedTypes(INamespaceSymbol ns)
    {
        var types = new List<INamedTypeSymbol>();
        CollectTypes(ns, types);
        return types;
    }

    private static void CollectTypes(INamespaceSymbol ns, List<INamedTypeSymbol> types)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            types.Add(type);
            CollectNestedTypes(type, types);
        }

        foreach (var childNs in ns.GetNamespaceMembers())
        {
            CollectTypes(childNs, types);
        }
    }

    private static void CollectNestedTypes(INamedTypeSymbol parent, List<INamedTypeSymbol> types)
    {
        foreach (var nested in parent.GetTypeMembers())
        {
            types.Add(nested);
            CollectNestedTypes(nested, types);
        }
    }
}
