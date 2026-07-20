using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

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
internal sealed class StaticDispatchCallExtractor(PolymorphismExtractionContext context)
{
    internal List<EdgeRecord> Extract(List<INamedTypeSymbol> allTypes)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var (methodSymbol, methodSyntax) in EnumerateMethodDeclarations(allTypes))
        {
            var bodySyntax = GetMethodBody(methodSyntax);
            if (bodySyntax == null)
                continue;

            var semanticModel = context.GetOrCreateSemanticModel(methodSyntax.SyntaxTree);
            var callerId = context.MakeSymbolId(methodSymbol);
            if (callerId == null)
                continue;

            foreach (var invocation in bodySyntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                TryEmitStaticCallEdge(invocation, semanticModel, callerId, edges, seen);
            }
        }

        return edges;
    }

    private void TryEmitStaticCallEdge(InvocationExpressionSyntax invocation, SemanticModel semanticModel, string callerId,
        List<EdgeRecord> edges, HashSet<(string source, string target, string kind)> seen)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
        if (symbolInfo.Symbol is not IMethodSymbol callee || callee.MethodKind == MethodKind.AnonymousFunction)
            return;

        // A "dispatch point" is a method whose runtime target requires
        // polymorphic resolution: interface methods, abstract methods,
        // and virtual (non-sealed) methods.
        bool isDispatchPoint = callee.ContainingType?.TypeKind == TypeKind.Interface
            || callee.IsAbstract
            || (callee.IsVirtual && !callee.IsSealed);

        if (!isDispatchPoint)
            return;

        var calleeId = context.MakeSymbolId(callee);
        if (calleeId == null || calleeId == callerId)
            return;

        var key = (callerId, calleeId, EdgeKind.StaticallyCalls.ToString());
        if (!seen.Add(key))
            return;

        var loc = PolymorphismExtractionContext.GetLocationInfo(invocation.GetLocation());
        edges.Add(new EdgeRecord
        {
            SourceSymbolId = callerId,
            TargetSymbolId = calleeId,
            Kind = EdgeKind.StaticallyCalls.ToString(),
            Provenance = "compiler_proved",
            SnapshotId = context.SnapshotId,
            ExtractorVersion = ExtractorConstants.StaticallyCallsExtractor,
            SourceDocumentPath = loc.path,
            SourceStartLine = loc.startLine,
            SourceStartColumn = loc.startColumn,
            SourceEndLine = loc.endLine,
            SourceEndColumn = loc.endColumn,
        });
    }

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
}
