using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

internal sealed class SymbolExtractionContext(
    Compilation compilation,
    IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)> documentContents,
    IReadOnlyDictionary<DocumentId, DocumentVersionId> documentVersions,
    IReadOnlySet<DocumentId> generatedDocuments,
    string snapshotId,
    IReadOnlySet<string>? scopeDocuments = null)
{
    internal Compilation Compilation { get; } = compilation;
    internal IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)> DocumentContents { get; } = documentContents;
    internal IReadOnlyDictionary<DocumentId, DocumentVersionId> DocumentVersions { get; } = documentVersions;
    internal IReadOnlySet<DocumentId> GeneratedDocuments { get; } = generatedDocuments;
    internal string AssemblyIdentity { get; } = compilation.Assembly.Identity.GetDisplayName();
    internal string SnapshotId { get; } = snapshotId;
    internal IReadOnlySet<string>? ScopeDocuments { get; } = scopeDocuments;

    internal bool IsInScope(SyntaxTree? syntaxTree)
    {
        if (ScopeDocuments == null || syntaxTree == null)
            return true;
        var filePath = syntaxTree.FilePath;
        if (string.IsNullOrEmpty(filePath))
            return true;
        return ScopeDocuments.Contains(filePath.Replace('\\', '/'));
    }

    internal DocumentId? ResolveDocumentId(SyntaxTree syntaxTree)
    {
        var filePath = syntaxTree.FilePath;
        if (string.IsNullOrEmpty(filePath))
            return null;

        var normalized = filePath.Replace('\\', '/');

        foreach (var docId in DocumentContents.Keys)
        {
            var docPath = docId.ToString().Replace('\\', '/');
            if (docPath == normalized || docPath.EndsWith("/" + normalized) || normalized.EndsWith("/" + docPath))
                return docId;
        }

        return null;
    }

    internal static IEnumerable<INamedTypeSymbol> GetNamespaceTypeMembers(INamespaceSymbol ns)
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
