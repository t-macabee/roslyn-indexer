using Microsoft.CodeAnalysis;
using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class PolymorphismExtractionContext
{
    private readonly Dictionary<SyntaxTree, SemanticModel> _semanticModelCache = [];
    private readonly string _gitRoot;

    internal PolymorphismExtractionContext(Compilation compilation, string snapshotId, string gitRoot, IReadOnlySet<string>? scopeDocuments = null)
    {
        Compilation = compilation;
        SnapshotId = snapshotId;
        _gitRoot = gitRoot ?? throw new ArgumentNullException(nameof(gitRoot));
        AssemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
        ScopeDocuments = scopeDocuments;
    }

    internal Compilation Compilation { get; }
    internal string SnapshotId { get; }
    internal string AssemblyIdentity { get; }
    internal IReadOnlySet<string>? ScopeDocuments { get; }

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
        return SymbolIdFactory.Make(symbol, AssemblyIdentity);
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

    internal string? GetDocumentPath(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return null;
        var path = syntaxRef.SyntaxTree?.FilePath;
        if (string.IsNullOrEmpty(path))
            return null;
        return DocumentChangeDetector.GetRelativePath(path, _gitRoot);
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

    internal (string? path, int? startLine, int? startColumn, int? endLine, int? endColumn)
        GetLocationInfo(Location location)
    {
        if (location == null || !location.IsInSource)
            return (null, null, null, null, null);

        var lineSpan = location.GetLineSpan();
        var filePath = location.SourceTree?.FilePath;
        var relativePath = string.IsNullOrEmpty(filePath) ? null : DocumentChangeDetector.GetRelativePath(filePath, _gitRoot);
        return (relativePath,
                lineSpan.StartLinePosition.Line,
                lineSpan.StartLinePosition.Character,
                lineSpan.EndLinePosition.Line,
                lineSpan.EndLinePosition.Character);
    }

    internal bool IsTypeInScope(INamedTypeSymbol typeSymbol)
    {
        if (ScopeDocuments == null)
            return true;
        foreach (var syntaxRef in typeSymbol.DeclaringSyntaxReferences)
        {
            var filePath = syntaxRef.SyntaxTree?.FilePath;
            if (filePath != null && ScopeDocuments.Contains(filePath.Replace('\\', '/')))
                return true;
        }
        return false;
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
