using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class UnknownPatternReflectionExtractor(ReflectionExtractionContext context)
{
    internal List<EdgeRecord> Extract(SyntaxNode root, SemanticModel semanticModel)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string pattern, string argument)>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            var pattern = ResolveReflectionPattern(memberAccess, invocation, semanticModel);
            if (pattern == null)
                continue;

            var sourceId = context.GetContainingMemberSymbolId(invocation, semanticModel);
            if (sourceId == null)
                continue;

            var argumentString = GetFirstStringLiteralArgument(invocation);
            var detailJson = JsonSerializer.Serialize(new { pattern, argument = argumentString ?? "" });

            var key = (sourceId, pattern, argumentString ?? "");
            if (!seen.Add(key))
                continue;

            var loc = context.GetLocationInfo(invocation.GetLocation());
            edges.Add(new EdgeRecord
            {
                SourceSymbolId = sourceId,
                TargetSymbolId = sourceId,
                Kind = EdgeKind.ReflectionTargetUnknown.ToString(),
                Provenance = Provenance.RuntimeUnknown,
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

    private static string? ResolveReflectionPattern(MemberAccessExpressionSyntax memberAccess, InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        var memberName = memberAccess.Name.Identifier.Text;

        switch (memberName)
        {
            case "GetType" when IsTypeGetType(invocation, semanticModel):
                return "Type.GetType";
            case "GetType":
            case "GetExportedTypes":
                var receiverType = semanticModel.GetTypeInfo(memberAccess.Expression);
                if (receiverType.Type != null && receiverType.Type.ToDisplayString() is "System.Reflection.Assembly" or "System.Type")
                {
                    return memberName == "GetExportedTypes" ? "Assembly.GetExportedTypes" : "Assembly.GetType";
                }
                return null;
            case "CreateInstance":
                var createReceiver = semanticModel.GetSymbolInfo(memberAccess.Expression);
                if (createReceiver.Symbol is INamedTypeSymbol namedType && namedType.ToDisplayString() == "System.Activator")
                {
                    return "Activator.CreateInstance";
                }
                return null;
            case "MakeGenericType":
                return "MakeGenericType";
            case "MakeGenericMethod":
                return "MakeGenericMethod";
            default:
                return null;
        }
    }

    private static string? GetFirstStringLiteralArgument(InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList.Arguments.Count == 0)
            return null;

        var firstArg = invocation.ArgumentList.Arguments[0].Expression;
        if (firstArg is LiteralExpressionSyntax lit && lit.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression))
            return lit.Token.ValueText;

        return null;
    }

    private static bool IsTypeGetType(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return false;

        var symbolInfo = semanticModel.GetSymbolInfo(memberAccess.Expression);
        if (symbolInfo.Symbol is INamedTypeSymbol namedType && namedType.ToDisplayString() == "System.Type")
        {
            return true;
        }

        if (memberAccess.Expression is IdentifierNameSyntax id && id.Identifier.Text == "Type")
        {
            return true;
        }

        return false;
    }
}
