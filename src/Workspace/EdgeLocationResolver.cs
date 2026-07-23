using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

public sealed class EdgeLocationResolver
{
    private readonly IReadOnlyDictionary<DocumentId, DocumentVersionId> _documentVersions;
    private readonly IReadOnlySet<DocumentId> _generatedDocuments;
    private readonly string _gitRoot;

    public EdgeLocationResolver(
        IReadOnlyDictionary<DocumentId, DocumentVersionId> documentVersions,
        IReadOnlySet<DocumentId> generatedDocuments,
        string gitRoot)
    {
        _documentVersions = documentVersions ?? throw new ArgumentNullException(nameof(documentVersions));
        _generatedDocuments = generatedDocuments ?? throw new ArgumentNullException(nameof(generatedDocuments));
        _gitRoot = gitRoot ?? throw new ArgumentNullException(nameof(gitRoot));
    }

    public (string? path, int? sl, int? sc, int? el, int? ec) Resolve(Location location)
    {
        if (location == null || !location.IsInSource)
            return (null, null, null, null, null);

        var lineSpan = location.GetLineSpan();
        var path = ResolveDocumentPath(location.SourceTree);
        return (path, lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character, lineSpan.EndLinePosition.Line, lineSpan.EndLinePosition.Character);
    }

    public (string? path, int? sl, int? sc, int? el, int? ec) Resolve(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
            return (null, null, null, null, null);

        return Resolve(syntaxRef.GetSyntax().GetLocation());
    }

    public bool IsGenerated(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var docId = new DocumentId(path);
        if (_generatedDocuments.Contains(docId))
            return true;

        var normalized = path.Replace('\\', '/');

        if (normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            return true;

        if (normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/generated/", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
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

        // For relative paths (e.g. in unit tests with ParseText), return as-is.
        // Production paths are always absolute (real project file paths).
        if (!Path.IsPathRooted(filePath))
            return normalized;

        return DocumentChangeDetector.GetRelativePath(filePath, _gitRoot);
    }
}
