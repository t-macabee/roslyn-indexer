using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

internal static class SymbolIdFactory
{
    // Always prefer the symbol's OWN assembly; fall back to the ambient
    // compilation identity only when ContainingAssembly is null (e.g. some
    // constructed/error symbols).
    internal static string? Make(ISymbol symbol, string ambientAssemblyIdentity)
    {
        var docCommentId = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(docCommentId))
            return null;
        var identity = symbol.ContainingAssembly?.Identity.GetDisplayName() ?? ambientAssemblyIdentity;
        return $"{docCommentId}|{identity}";
    }
}
