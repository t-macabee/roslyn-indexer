using Lurp.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

public sealed partial class MemberEdgeExtractor(Compilation compilation, IReadOnlyDictionary<DocumentId, DocumentVersionId> documentVersions, IReadOnlySet<DocumentId> generatedDocuments, string snapshotId)
{
    private readonly Compilation _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
    private readonly IReadOnlyDictionary<DocumentId, DocumentVersionId> _documentVersions = documentVersions ?? throw new ArgumentNullException(nameof(documentVersions));
    private readonly IReadOnlySet<DocumentId> _generatedDocuments = generatedDocuments ?? throw new ArgumentNullException(nameof(generatedDocuments));
    private readonly string _snapshotId = snapshotId ?? throw new ArgumentNullException(nameof(snapshotId));
    private readonly string _assemblyIdentity = compilation.Assembly.Identity.GetDisplayName();

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

    private EdgeRecord MakeEdge(string sourceId, string targetId, string kind, string extractorVersion, (string? path, int? sl, int? sc, int? el, int? ec)? location)
    {
        var sourceDocumentPath = location?.path;
        var isSourceGenerated = IsGeneratedDocument(sourceDocumentPath);

        var provenance = "compiler_proved";
        if (isSourceGenerated)
        {
            provenance += ":cross_generated";
        }

        return new EdgeRecord
        {
            SourceSymbolId = sourceId,
            TargetSymbolId = targetId,
            Kind = kind,
            Provenance = provenance,
            SnapshotId = _snapshotId,
            ExtractorVersion = extractorVersion,
            SourceDocumentPath = sourceDocumentPath,
            SourceStartLine = location?.sl,
            SourceStartColumn = location?.sc,
            SourceEndLine = location?.el,
            SourceEndColumn = location?.ec,
        };
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
        return (path, lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character, lineSpan.EndLinePosition.Line, lineSpan.EndLinePosition.Character);
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
            if (docPath == normalized || docPath.EndsWith("/" + normalized, StringComparison.Ordinal) ||
                normalized.EndsWith("/" + docPath, StringComparison.Ordinal))
            {
                return docPath;
            }
        }

        return normalized;
    }

    private SemanticModel GetOrCreateSemanticModel(SyntaxTree syntaxTree, Dictionary<SyntaxTree, SemanticModel> cache)
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
