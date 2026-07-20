using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lurp.Workspace;

internal sealed class ReflectionExtractionContext
{
    private readonly Dictionary<SyntaxTree, SemanticModel> _semanticModelCache = [];

    internal ReflectionExtractionContext(Compilation compilation, string snapshotId)
    {
        Compilation = compilation;
        SnapshotId = snapshotId;
        AssemblyIdentity = compilation.Assembly.Identity.GetDisplayName();

        KnownTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        KnownMemberNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectKnownNames(compilation.Assembly.GlobalNamespace, KnownTypeNames, KnownMemberNames);
    }

    internal Compilation Compilation { get; }
    internal string SnapshotId { get; }
    internal string AssemblyIdentity { get; }
    internal HashSet<string> KnownTypeNames { get; }
    internal HashSet<string> KnownMemberNames { get; }

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

    internal string? GetContainingMemberSymbolId(SyntaxNode node, SemanticModel semanticModel)
    {
        for (var current = node.Parent; current != null; current = current.Parent)
        {
            ISymbol? memberSymbol = null;

            if (current is MethodDeclarationSyntax)
            {
                memberSymbol = semanticModel.GetDeclaredSymbol(current) as IMethodSymbol;
            }
            else if (current is PropertyDeclarationSyntax)
            {
                memberSymbol = semanticModel.GetDeclaredSymbol(current) as IPropertySymbol;
            }
            else if (current is ConstructorDeclarationSyntax)
            {
                memberSymbol = semanticModel.GetDeclaredSymbol(current) as IMethodSymbol;
            }
            else if (current is FieldDeclarationSyntax fieldDecl)
            {
                var firstVariable = fieldDecl.Declaration.Variables.FirstOrDefault();
                if (firstVariable != null)
                {
                    memberSymbol = semanticModel.GetDeclaredSymbol(firstVariable) as IFieldSymbol;
                }
            }

            if (memberSymbol != null)
                return MakeSymbolId(memberSymbol);
        }

        return null;
    }

    internal static (string? path, int? startLine, int? startColumn, int? endLine, int? endColumn)
        GetLocationInfo(Location location)
    {
        if (location == null || !location.IsInSource)
            return (null, null, null, null, null);

        var lineSpan = location.GetLineSpan();
        return (location.SourceTree?.FilePath, lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character, lineSpan.EndLinePosition.Line, lineSpan.EndLinePosition.Character);
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

    private static void CollectKnownNames(INamespaceSymbol ns, HashSet<string> typeNames, HashSet<string> memberNames)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            typeNames.Add(type.Name);
            foreach (var member in type.GetMembers())
            {
                memberNames.Add(member.Name);
            }
        }

        foreach (var childNs in ns.GetNamespaceMembers())
        {
            CollectKnownNames(childNs, typeNames, memberNames);
        }
    }
}
