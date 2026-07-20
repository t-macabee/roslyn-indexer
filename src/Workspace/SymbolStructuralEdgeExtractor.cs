using Microsoft.CodeAnalysis;
using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class SymbolStructuralEdgeExtractor(SymbolExtractionContext context)
{
    internal List<EdgeRecord> ExtractEdges()
    {
        var edges = new List<EdgeRecord>();

        foreach (var typeSymbol in SymbolExtractionContext.GetNamespaceTypeMembers(context.Compilation.Assembly.GlobalNamespace))
        {
            CollectTypeEdges(typeSymbol, edges);
        }

        return edges;
    }

    private void CollectTypeEdges(INamedTypeSymbol typeSymbol, List<EdgeRecord> edges)
    {
        var sourceId = MakeSymbolId(typeSymbol);
        if (sourceId == null)
            return;

        if (typeSymbol.BaseType != null && typeSymbol.BaseType.SpecialType != SpecialType.System_Object && typeSymbol.BaseType.SpecialType != SpecialType.System_ValueType)
        {
            var targetId = MakeSymbolId(typeSymbol.BaseType);
            if (targetId != null)
            {
                edges.Add(MakeEdge(sourceId, targetId, EdgeKind.Inherits.ToString(), typeSymbol));
            }
        }

        foreach (var iface in typeSymbol.Interfaces)
        {
            var targetId = MakeSymbolId(iface);
            if (targetId != null)
            {
                edges.Add(MakeEdge(sourceId, targetId, EdgeKind.Implements.ToString(), typeSymbol));
            }
        }

        foreach (var nested in typeSymbol.GetTypeMembers())
        {
            CollectTypeEdges(nested, edges);
            var nestedId = MakeSymbolId(nested);
            if (nestedId != null)
            {
                edges.Add(MakeEdge(sourceId, nestedId, EdgeKind.Contains.ToString(), typeSymbol));
            }
        }

        foreach (var member in typeSymbol.GetMembers())
        {
            if (member is INamedTypeSymbol)
                continue;

            CollectMemberReferenceEdges(member, sourceId, edges);
        }
    }

    private void CollectMemberReferenceEdges(ISymbol member, string sourceSymbolId, List<EdgeRecord> edges)
    {
        ITypeSymbol? referencedType = member switch
        {
            IMethodSymbol method => method.ReturnType,
            IPropertySymbol prop => prop.Type,
            IFieldSymbol field => field.Type,
            IEventSymbol evt => evt.Type,
            _ => null
        };

        if (referencedType is INamedTypeSymbol namedType)
        {
            var targetId = MakeSymbolId(namedType);
            if (targetId != null && targetId != sourceSymbolId)
            {
                edges.Add(MakeEdge(sourceSymbolId, targetId, EdgeKind.References.ToString(), member));
            }
        }
    }

    private string? MakeSymbolId(ITypeSymbol typeSymbol)
    {
        var docCommentId = typeSymbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(docCommentId))
            return null;
        return $"{docCommentId}|{context.AssemblyIdentity}";
    }

    private EdgeRecord MakeEdge(string sourceId, string targetId, string kind, ISymbol sourceSymbol)
    {
        var loc = GetSymbolSourceLocation(sourceSymbol);
        return new EdgeRecord
        {
            SourceSymbolId = sourceId,
            TargetSymbolId = targetId,
            Kind = kind,
            Provenance = "roslyn",
            SnapshotId = context.SnapshotId,
            ExtractorVersion = VersionConstants.ExtractorVersion,
            SourceDocumentPath = loc?.path,
            SourceStartLine = loc?.startLine,
            SourceStartColumn = loc?.startColumn,
            SourceEndLine = loc?.endLine,
            SourceEndColumn = loc?.endColumn,
        };
    }

    private (string? path, int? startLine, int? startColumn, int? endLine, int? endColumn)?
        GetSymbolSourceLocation(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var syntaxTree = syntaxRef.SyntaxTree;
        if (syntaxTree == null)
            return null;
        var documentId = context.ResolveDocumentId(syntaxTree);
        if (documentId == null)
            return null;
        var location = syntaxRef.GetSyntax().GetLocation();
        if (location == null || !location.IsInSource)
            return null;
        var lineSpan = location.GetLineSpan();
        return (documentId.Value.ToString(),
                lineSpan.StartLinePosition.Line,
                lineSpan.StartLinePosition.Character,
                lineSpan.EndLinePosition.Line,
                lineSpan.EndLinePosition.Character);
    }
}
