using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class StringLiteralReflectionExtractor(ReflectionExtractionContext context)
{
    internal List<EdgeRecord> Extract(SyntaxNode root, SemanticModel semanticModel)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
                continue;

            var text = literal.Token.ValueText;
            if (string.IsNullOrEmpty(text) || text.Length < 3)
                continue;

            if (IsNoiseString(text))
                continue;

            string? matchedSymbolId = null;
            string? matchedName = null;

            if (context.KnownTypeNames.Contains(text))
            {
                matchedName = text;
                matchedSymbolId = ResolveSymbolIdByName(text, isType: true);
            }
            else if (context.KnownMemberNames.Contains(text))
            {
                matchedName = text;
                matchedSymbolId = ResolveSymbolIdByName(text, isType: false);
            }

            if (matchedSymbolId == null)
                continue;

            var sourceId = context.GetContainingMemberSymbolId(literal, semanticModel);
            if (sourceId == null)
                continue;

            var key = (sourceId, matchedSymbolId, EdgeKind.ReflectionNameCandidate.ToString());
            if (!seen.Add(key))
                continue;

            var detailJson = JsonSerializer.Serialize(new { literal_value = text, matched_name = matchedName });

            var loc = ReflectionExtractionContext.GetLocationInfo(literal.GetLocation());

            edges.Add(new EdgeRecord
            {
                SourceSymbolId = sourceId,
                TargetSymbolId = matchedSymbolId,
                Kind = EdgeKind.ReflectionNameCandidate.ToString(),
                Provenance = "name_candidate",
                SnapshotId = context.SnapshotId,
                ExtractorVersion = ExtractorConstants.ReflectionExtractor,
                SourceDocumentPath = loc.path,
                SourceStartLine = loc.startLine,
                SourceStartColumn = loc.startColumn,
                SourceEndLine = loc.endLine,
                SourceEndColumn = loc.endColumn,
            });
        }

        return edges;
    }

    private static bool IsNoiseString(string text)
    {
        if (text.All(char.IsDigit))
            return true;
        if (text.Contains(' ') && !text.Contains('.') && !IsPascalCase(text) && !IsCamelCase(text))
            return true;
        return false;
    }

    private static bool IsPascalCase(string text) =>
        text.Length > 0 && char.IsUpper(text[0]) && text.Any(char.IsLower);

    private static bool IsCamelCase(string text) =>
        text.Length > 0 && char.IsLower(text[0]) && text.Any(char.IsUpper);

    private string? ResolveSymbolIdByName(string name, bool isType)
    {
        foreach (var typeSymbol in ReflectionExtractionContext.GetNamespaceTypeMembers(context.Compilation.Assembly.GlobalNamespace))
        {
            if (isType)
            {
                if (string.Equals(typeSymbol.Name, name, StringComparison.OrdinalIgnoreCase))
                    return context.MakeSymbolId(typeSymbol);
            }
            else
            {
                foreach (var member in typeSymbol.GetMembers())
                {
                    if (string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase))
                        return context.MakeSymbolId(member);
                }
            }
        }
        return null;
    }
}
