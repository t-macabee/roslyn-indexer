using Lurp.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp;

public sealed class MemberEdgeExtractor
{
    private readonly Compilation _compilation;
    private readonly IReadOnlyDictionary<DocumentId, DocumentVersionId> _documentVersions;
    private readonly IReadOnlySet<DocumentId> _generatedDocuments;
    private readonly string _snapshotId;
    private readonly string _assemblyIdentity;

    public MemberEdgeExtractor(
        Compilation compilation,
        IReadOnlyDictionary<DocumentId, DocumentVersionId> documentVersions,
        IReadOnlySet<DocumentId> generatedDocuments,
        string snapshotId)
    {
        _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        _documentVersions = documentVersions ?? throw new ArgumentNullException(nameof(documentVersions));
        _generatedDocuments = generatedDocuments ?? throw new ArgumentNullException(nameof(generatedDocuments));
        _snapshotId = snapshotId ?? throw new ArgumentNullException(nameof(snapshotId));
        _assemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
    }

    public List<EdgeRecord> ExtractAll()
    {
        var allEdges = new List<EdgeRecord>();

        allEdges.AddRange(ExtractDeclares());
        allEdges.AddRange(ExtractCalls());
        allEdges.AddRange(ExtractConstructs());
        allEdges.AddRange(ExtractOverrides());
        allEdges.AddRange(ExtractReadsWrites());
        allEdges.AddRange(ExtractReturns());
        allEdges.AddRange(ExtractParameterDependencies());
        allEdges.AddRange(ExtractThrows());

        return allEdges;
    }

    private List<EdgeRecord> ExtractDeclares()
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var typeSymbol in GetNamespaceTypeMembers(_compilation.Assembly.GlobalNamespace))
        {
            var typeId = MakeSymbolId(typeSymbol);
            if (typeId == null)
                continue;

            foreach (var member in typeSymbol.GetMembers())
            {
                if (member is INamedTypeSymbol)
                    continue;

                var memberId = MakeSymbolId(member);
                if (memberId == null)
                    continue;

                var key = (typeId, memberId, EdgeKind.Declares.ToString());
                if (!seen.Add(key))
                    continue;

                var loc = GetMemberSourceLocation(member);
                edges.Add(new EdgeRecord(
                    sourceSymbolId: typeId,
                    targetSymbolId: memberId,
                    kind: EdgeKind.Declares.ToString(),
                    provenance: "compiler_proved",
                    snapshotId: _snapshotId,
                    extractorVersion: ExtractorConstants.DeclaresExtractor,
                    sourceDocumentPath: loc?.path,
                    sourceStartLine: loc?.startLine,
                    sourceStartColumn: loc?.startColumn,
                    sourceEndLine: loc?.endLine,
                    sourceEndColumn: loc?.endColumn));
            }
        }

        return edges;
    }

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
                    edges.Add(MakeEdge(callerId, calleeId, EdgeKind.Calls.ToString(),
                        ExtractorConstants.CallsExtractor, loc));
                }
            }
        }

        return edges;
    }

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

    private List<EdgeRecord> ExtractOverrides()
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var typeSymbol in GetNamespaceTypeMembers(_compilation.Assembly.GlobalNamespace))
        {
            foreach (var member in typeSymbol.GetMembers())
            {
                (string? sourceId, string? targetId) = member switch
                {
                    IMethodSymbol method when method.IsOverride && method.OverriddenMethod != null
                        => (MakeSymbolId(method), MakeSymbolId(method.OverriddenMethod)),
                    IPropertySymbol prop when prop.IsOverride && prop.OverriddenProperty != null
                        => (MakeSymbolId(prop), MakeSymbolId(prop.OverriddenProperty)),
                    _ => ((string?)null, (string?)null)
                };

                if (sourceId == null || targetId == null)
                    continue;

                var key = (sourceId, targetId, EdgeKind.Overrides.ToString());
                if (!seen.Add(key))
                    continue;

                var loc = GetMemberSourceLocation(member);
                edges.Add(MakeEdge(sourceId, targetId, EdgeKind.Overrides.ToString(),
                    ExtractorConstants.OverridesExtractor, loc));
            }
        }

        return edges;
    }

    private List<EdgeRecord> ExtractReadsWrites()
    {
        var edges = new List<EdgeRecord>();
        var seenReads = new HashSet<(string source, string target, string kind)>();
        var seenWrites = new HashSet<(string source, string target, string kind)>();
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

            var accesses = bodySyntax.DescendantNodes()
                .Where(n => n is IdentifierNameSyntax or MemberAccessExpressionSyntax);

            foreach (var access in accesses)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(access);
                if (symbolInfo.Symbol is not IFieldSymbol and not IPropertySymbol)
                    continue;

                var memberSymbol = symbolInfo.Symbol;
                var memberId = MakeSymbolId(memberSymbol);
                if (memberId == null)
                    continue;

                bool isWrite = IsWriteContext(access);
                var kind = isWrite ? EdgeKind.Writes.ToString() : EdgeKind.Reads.ToString();
                var seenSet = isWrite ? seenWrites : seenReads;

                var key = (callerId, memberId, kind);
                if (!seenSet.Add(key))
                    continue;

                var loc = GetLocationInfo(access.GetLocation());
                edges.Add(MakeEdge(callerId, memberId, kind,
                    ExtractorConstants.ReadsWritesExtractor, loc));
            }
        }

        return edges;
    }

    private static bool IsWriteContext(SyntaxNode node)
    {
        if (node.Parent is AssignmentExpressionSyntax assign)
            return assign.Left == node;

        if (node.Parent is PrefixUnaryExpressionSyntax preUnary &&
            (preUnary.IsKind(SyntaxKind.PreIncrementExpression) ||
             preUnary.IsKind(SyntaxKind.PreDecrementExpression)))
        {
            return preUnary.Operand == node;
        }

        if (node.Parent is PostfixUnaryExpressionSyntax postUnary &&
            (postUnary.IsKind(SyntaxKind.PostIncrementExpression) ||
             postUnary.IsKind(SyntaxKind.PostDecrementExpression)))
        {
            return postUnary.Operand == node;
        }

        if (node.Parent is ArgumentSyntax arg &&
            (arg.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword) ||
             arg.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword)))
        {
            return true;
        }

        return false;
    }

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

    private List<EdgeRecord> ExtractThrows()
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

            foreach (var throwStmt in bodySyntax.DescendantNodes().OfType<ThrowStatementSyntax>())
            {
                if (throwStmt.Expression == null)
                    continue;

                var exceptionType = ResolveThrownType(throwStmt.Expression, semanticModel);
                if (exceptionType == null)
                    continue;

                var typeId = MakeSymbolId(exceptionType);
                if (typeId == null)
                    continue;

                var key = (callerId, typeId, EdgeKind.Throws.ToString());
                if (!seen.Add(key))
                    continue;

                var loc = GetLocationInfo(throwStmt.GetLocation());
                edges.Add(MakeEdge(callerId, typeId, EdgeKind.Throws.ToString(),
                    ExtractorConstants.ThrowsExtractor, loc));
            }

            foreach (var throwExpr in bodySyntax.DescendantNodes().OfType<ThrowExpressionSyntax>())
            {
                if (throwExpr.Expression == null)
                    continue;

                var exceptionType = ResolveThrownType(throwExpr.Expression, semanticModel);
                if (exceptionType == null)
                    continue;

                var typeId = MakeSymbolId(exceptionType);
                if (typeId == null)
                    continue;

                var key = (callerId, typeId, EdgeKind.Throws.ToString());
                if (!seen.Add(key))
                    continue;

                var loc = GetLocationInfo(throwExpr.GetLocation());
                edges.Add(MakeEdge(callerId, typeId, EdgeKind.Throws.ToString(),
                    ExtractorConstants.ThrowsExtractor, loc));
            }
        }

        return edges;
    }

    private static INamedTypeSymbol? ResolveThrownType(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        if (expression is ObjectCreationExpressionSyntax creation)
        {
            var symbolInfo = semanticModel.GetSymbolInfo(creation);
            if (symbolInfo.Symbol is IMethodSymbol ctor)
                return ctor.ContainingType;
        }

        var typeInfo = semanticModel.GetTypeInfo(expression);
        return typeInfo.Type as INamedTypeSymbol;
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

    private IEnumerable<(IMethodSymbol, CSharpSyntaxNode)> EnumerateMethodDeclarations()
    {
        foreach (var typeSymbol in GetNamespaceTypeMembers(_compilation.Assembly.GlobalNamespace))
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

    private string? MakeSymbolId(ISymbol symbol)
    {
        var docCommentId = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(docCommentId))
            return null;
        return $"{docCommentId}|{_assemblyIdentity}";
    }

    private EdgeRecord MakeEdge(string sourceId, string targetId, string kind,
        string extractorVersion,
        (string? path, int? sl, int? sc, int? el, int? ec)? location)
    {
        var sourceDocumentPath = location?.path;
        var isSourceGenerated = IsGeneratedDocument(sourceDocumentPath);

        var provenance = "compiler_proved";
        if (isSourceGenerated)
        {
            provenance += ":cross_generated";
        }

        return new EdgeRecord(
            sourceSymbolId: sourceId,
            targetSymbolId: targetId,
            kind: kind,
            provenance: provenance,
            snapshotId: _snapshotId,
            extractorVersion: extractorVersion,
            sourceDocumentPath: location?.path,
            sourceStartLine: location?.sl,
            sourceStartColumn: location?.sc,
            sourceEndLine: location?.el,
            sourceEndColumn: location?.ec);
    }

    private bool IsGeneratedDocument(string? documentPath)
    {
        if (string.IsNullOrEmpty(documentPath))
            return false;

        
        var docId = new DocumentId(documentPath);
        if (_generatedDocuments.Contains(docId))
            return true;

        var normalized = documentPath.Replace('\\', '/');

        if (normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            return true;

        if (normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/generated/", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private (string? path, int? startLine, int? startColumn, int? endLine, int? endColumn)?
        GetMemberSourceLocation(ISymbol member)
    {
        var syntaxRef = member.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;

        return GetLocationInfo(syntaxRef.GetSyntax().GetLocation());
    }

    private (string? path, int? startLine, int? startColumn, int? endLine, int? endColumn)
        GetLocationInfo(Location location)
    {
        if (location == null || !location.IsInSource)
            return (null, null, null, null, null);

        var lineSpan = location.GetLineSpan();
        var path = ResolveDocumentPath(location.SourceTree);
        return (path,
                lineSpan.StartLinePosition.Line,
                lineSpan.StartLinePosition.Character,
                lineSpan.EndLinePosition.Line,
                lineSpan.EndLinePosition.Character);
    }

    private string? ResolveDocumentPath(SyntaxTree? syntaxTree)
    {
        if (syntaxTree == null)
            return null;

        var filePath = syntaxTree.FilePath;
        if (string.IsNullOrEmpty(filePath))
            return null;

        var normalized = filePath.Replace('\\', '/');

        foreach (var docId in _documentVersions.Keys)
        {
            var docPath = docId.ToString().Replace('\\', '/');
            if (docPath == normalized ||
                docPath.EndsWith("/" + normalized, StringComparison.Ordinal) ||
                normalized.EndsWith("/" + docPath, StringComparison.Ordinal))
            {
                return docPath;
            }
        }

        return normalized;
    }

    private SemanticModel GetOrCreateSemanticModel(
        SyntaxTree syntaxTree,
        Dictionary<SyntaxTree, SemanticModel> cache)
    {
        if (!cache.TryGetValue(syntaxTree, out var model))
        {
            model = _compilation.GetSemanticModel(syntaxTree);
            cache[syntaxTree] = model;
        }
        return model;
    }

    private static IEnumerable<INamedTypeSymbol> GetNamespaceTypeMembers(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
        }

        foreach (var childNs in ns.GetNamespaceMembers())
        {
            foreach (var type in GetNamespaceTypeMembers(childNs))
            {
                yield return type;
            }
        }
    }
}
