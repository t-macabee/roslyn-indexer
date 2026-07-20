using Microsoft.CodeAnalysis;
using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class PolymorphismExtractionContext
{
    private readonly Dictionary<SyntaxTree, SemanticModel> _semanticModelCache = [];

    internal PolymorphismExtractionContext(Compilation compilation, string snapshotId)
    {
        Compilation = compilation;
        SnapshotId = snapshotId;
        AssemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
    }

    internal Compilation Compilation { get; }
    internal string SnapshotId { get; }
    internal string AssemblyIdentity { get; }

    internal SemanticModel GetOrCreateSemanticModel(SyntaxTree syntaxTree)
    {
        if (!_semanticModelCache.TryGetValue(syntaxTree, out var model))
        {
            model = Compilation.GetSemanticModel(syntaxTree);
            _semanticModelCache[syntaxTree] = model;
        }
        return model;
    }

    internal string? MakeSymbolId(ISymbol symbol)
    {
        var docCommentId = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(docCommentId))
            return null;
        return $"{docCommentId}|{AssemblyIdentity}";
    }

    internal EdgeRecord MakeMayDispatchEdge(string sourceId, string targetId, ISymbol targetSymbol, string provenance)
    {
        return new EdgeRecord
        {
            SourceSymbolId = sourceId,
            TargetSymbolId = targetId,
            Kind = EdgeKind.MayDispatchTo.ToString(),
            Provenance = provenance,
            SnapshotId = SnapshotId,
            ExtractorVersion = ExtractorConstants.PolymorphismExtractor,
            SourceDocumentPath = GetDocumentPath(targetSymbol),
            SourceStartLine = GetStartLine(targetSymbol),
            SourceStartColumn = GetStartColumn(targetSymbol),
            SourceEndLine = GetEndLine(targetSymbol),
            SourceEndColumn = GetEndColumn(targetSymbol),
        };
    }

    internal static string? GetDocumentPath(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var path = syntaxRef.SyntaxTree?.FilePath;
        return string.IsNullOrEmpty(path) ? null : path.Replace('\\', '/');
    }

    internal static int? GetStartLine(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var span = syntaxRef.GetSyntax().GetLocation().GetLineSpan();
        return span.StartLinePosition.Line;
    }

    internal static int? GetStartColumn(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var span = syntaxRef.GetSyntax().GetLocation().GetLineSpan();
        return span.StartLinePosition.Character;
    }

    internal static int? GetEndLine(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var span = syntaxRef.GetSyntax().GetLocation().GetLineSpan();
        return span.EndLinePosition.Line;
    }

    internal static int? GetEndColumn(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var span = syntaxRef.GetSyntax().GetLocation().GetLineSpan();
        return span.EndLinePosition.Character;
    }

    internal static (string? path, int? startLine, int? startColumn, int? endLine, int? endColumn)
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

    internal static List<INamedTypeSymbol> GetAllNamedTypes(INamespaceSymbol ns)
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
