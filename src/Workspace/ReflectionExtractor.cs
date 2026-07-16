using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Lurp.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp;

public sealed class ReflectionExtractor
{
    private readonly Compilation _compilation;
    private readonly string _snapshotId;
    private readonly string _assemblyIdentity;

    public ReflectionExtractor(Compilation compilation, string snapshotId)
    {
        _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        _snapshotId = snapshotId ?? throw new ArgumentNullException(nameof(snapshotId));
        _assemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
    }

    public List<EdgeRecord> Extract()
    {
        var edges = new List<EdgeRecord>();
        var semanticModelCache = new Dictionary<SyntaxTree, SemanticModel>();

        
        var knownTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownMemberNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectKnownNames(_compilation.Assembly.GlobalNamespace, knownTypeNames, knownMemberNames);

        foreach (var syntaxTree in _compilation.SyntaxTrees)
        {
            var semanticModel = GetOrCreateSemanticModel(syntaxTree, semanticModelCache);
            var root = syntaxTree.GetRoot();

            
            edges.AddRange(ExtractTypeOfExpressions(root, semanticModel));

            
            edges.AddRange(ExtractNameOfExpressions(root, semanticModel));

            
            edges.AddRange(ExtractStringLiteralCandidates(root, semanticModel, knownTypeNames, knownMemberNames));

            
            edges.AddRange(ExtractUnknownPatterns(root, semanticModel));
        }

        return edges;
    }

    
    
    

    private List<EdgeRecord> ExtractTypeOfExpressions(SyntaxNode root, SemanticModel semanticModel)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var typeOfExpr in root.DescendantNodes().OfType<TypeOfExpressionSyntax>())
        {
            var typeInfo = semanticModel.GetTypeInfo(typeOfExpr.Type);
            if (typeInfo.Type == null)
                continue;

            var targetId = MakeSymbolId(typeInfo.Type);
            if (targetId == null)
                continue;

            var sourceId = GetContainingMemberSymbolId(typeOfExpr, semanticModel);
            if (sourceId == null)
                continue;

            var key = (sourceId, targetId, EdgeKind.ReflectionTypeRef.ToString());
            if (!seen.Add(key))
                continue;

            var loc = GetLocationInfo(typeOfExpr.GetLocation());
            edges.Add(new EdgeRecord(
                sourceSymbolId: sourceId,
                targetSymbolId: targetId,
                kind: EdgeKind.ReflectionTypeRef.ToString(),
                provenance: "compiler_proved",
                snapshotId: _snapshotId,
                extractorVersion: ExtractorConstants.ReflectionExtractor,
                sourceDocumentPath: loc.path,
                sourceStartLine: loc.startLine,
                sourceStartColumn: loc.startColumn,
                sourceEndLine: loc.endLine,
                sourceEndColumn: loc.endColumn));
        }

        return edges;
    }

    
    
    

    private List<EdgeRecord> ExtractNameOfExpressions(SyntaxNode root, SemanticModel semanticModel)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not IdentifierNameSyntax identifier)
                continue;

            if (!identifier.Identifier.Text.Equals("nameof", StringComparison.Ordinal))
                continue;

            if (invocation.ArgumentList.Arguments.Count != 1)
                continue;

            var argument = invocation.ArgumentList.Arguments[0].Expression;
            var sourceId = GetContainingMemberSymbolId(invocation, semanticModel);
            if (sourceId == null)
                continue;

            
            var symbolInfo = semanticModel.GetSymbolInfo(argument);
            var resolvedSymbol = symbolInfo.Symbol;
            if (resolvedSymbol == null && symbolInfo.CandidateSymbols.Length > 0)
            {
                resolvedSymbol = symbolInfo.CandidateSymbols[0];
            }

            if (resolvedSymbol != null && resolvedSymbol.CanBeReferencedByName)
            {
                var targetId = MakeSymbolId(resolvedSymbol);
                if (targetId == null)
                    continue;

                var key = (sourceId, targetId, EdgeKind.ReflectionMemberRef.ToString());
                if (!seen.Add(key))
                    continue;

                var loc = GetLocationInfo(invocation.GetLocation());
                edges.Add(new EdgeRecord(
                    sourceSymbolId: sourceId,
                    targetSymbolId: targetId,
                    kind: EdgeKind.ReflectionMemberRef.ToString(),
                    provenance: "compiler_proved",
                    snapshotId: _snapshotId,
                    extractorVersion: ExtractorConstants.ReflectionExtractor,
                    sourceDocumentPath: loc.path,
                    sourceStartLine: loc.startLine,
                    sourceStartColumn: loc.startColumn,
                    sourceEndLine: loc.endLine,
                    sourceEndColumn: loc.endColumn));
            }
            else
            {
                
                
                continue;
            }
        }

        return edges;
    }



    
    
    

    private List<EdgeRecord> ExtractStringLiteralCandidates(
        SyntaxNode root,
        SemanticModel semanticModel,
        HashSet<string> knownTypeNames,
        HashSet<string> knownMemberNames)
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

            if (knownTypeNames.Contains(text))
            {
                matchedName = text;
                matchedSymbolId = ResolveSymbolIdByName(text, semanticModel, isType: true);
            }
            else if (knownMemberNames.Contains(text))
            {
                matchedName = text;
                matchedSymbolId = ResolveSymbolIdByName(text, semanticModel, isType: false);
            }

            if (matchedSymbolId == null)
                continue;

            var sourceId = GetContainingMemberSymbolId(literal, semanticModel);
            if (sourceId == null)
                continue;

            var key = (sourceId, matchedSymbolId, EdgeKind.ReflectionNameCandidate.ToString());
            if (!seen.Add(key))
                continue;

            var detailJson = JsonSerializer.Serialize(new
            {
                literal_value = text,
                matched_name = matchedName
            });

            var loc = GetLocationInfo(literal.GetLocation());
            
            
            edges.Add(new EdgeRecord(
                sourceSymbolId: sourceId,
                targetSymbolId: matchedSymbolId,
                kind: EdgeKind.ReflectionNameCandidate.ToString(),
                provenance: "name_candidate",
                snapshotId: _snapshotId,
                extractorVersion: ExtractorConstants.ReflectionExtractor,
                sourceDocumentPath: loc.path,
                sourceStartLine: loc.startLine,
                sourceStartColumn: loc.startColumn,
                sourceEndLine: loc.endLine,
                sourceEndColumn: loc.endColumn));
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

    private string? ResolveSymbolIdByName(string name, SemanticModel semanticModel, bool isType)
    {
        
        foreach (var typeSymbol in GetNamespaceTypeMembers(_compilation.Assembly.GlobalNamespace))
        {
            if (isType)
            {
                if (string.Equals(typeSymbol.Name, name, StringComparison.OrdinalIgnoreCase))
                    return MakeSymbolId(typeSymbol);
            }
            else
            {
                foreach (var member in typeSymbol.GetMembers())
                {
                    if (string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase))
                        return MakeSymbolId(member);
                }
            }
        }
        return null;
    }

    
    
    

    private List<EdgeRecord> ExtractUnknownPatterns(SyntaxNode root, SemanticModel semanticModel)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string pattern, string argument)>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            var memberName = memberAccess.Name.Identifier.Text;
            var sourceId = GetContainingMemberSymbolId(invocation, semanticModel);
            if (sourceId == null)
                continue;

            string? argumentString = null;
            if (invocation.ArgumentList.Arguments.Count > 0)
            {
                var firstArg = invocation.ArgumentList.Arguments[0].Expression;
                if (firstArg is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    argumentString = lit.Token.ValueText;
                }
            }

            string? pattern = null;

            switch (memberName)
            {
                case "GetType" when IsTypeGetType(invocation, semanticModel):
                    pattern = "Type.GetType";
                    break;
                case "GetType":
                case "GetExportedTypes":
                    
                    var receiverType = semanticModel.GetTypeInfo(memberAccess.Expression);
                    if (receiverType.Type != null &&
                        receiverType.Type.ToDisplayString() is "System.Reflection.Assembly" or "System.Type")
                    {
                        pattern = memberName == "GetExportedTypes"
                            ? "Assembly.GetExportedTypes"
                            : "Assembly.GetType";
                    }
                    break;
                case "CreateInstance":
                    
                    var createReceiver = semanticModel.GetSymbolInfo(memberAccess.Expression);
                    if (createReceiver.Symbol is INamedTypeSymbol namedType &&
                        namedType.ToDisplayString() == "System.Activator")
                    {
                        pattern = "Activator.CreateInstance";
                    }
                    break;
                case "MakeGenericType":
                    pattern = "MakeGenericType";
                    break;
                case "MakeGenericMethod":
                    pattern = "MakeGenericMethod";
                    break;
            }

            if (pattern == null)
                continue;

            var detailJson = JsonSerializer.Serialize(new
            {
                pattern,
                argument = argumentString ?? ""
            });

            var key = (sourceId, pattern, argumentString ?? "");
            if (!seen.Add(key))
                continue;

            var loc = GetLocationInfo(invocation.GetLocation());
            edges.Add(new EdgeRecord(
                sourceSymbolId: sourceId,
                targetSymbolId: sourceId, 
                kind: EdgeKind.ReflectionTargetUnknown.ToString(),
                provenance: "runtime_unknown",
                snapshotId: _snapshotId,
                extractorVersion: ExtractorConstants.ReflectionExtractor,
                sourceDocumentPath: loc.path,
                sourceStartLine: loc.startLine,
                sourceStartColumn: loc.startColumn,
                sourceEndLine: loc.endLine,
                sourceEndColumn: loc.endColumn));
        }

        return edges;
    }

    private static bool IsTypeGetType(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return false;

        var symbolInfo = semanticModel.GetSymbolInfo(memberAccess.Expression);
        if (symbolInfo.Symbol is INamedTypeSymbol namedType &&
            namedType.ToDisplayString() == "System.Type")
        {
            return true;
        }

        
        if (memberAccess.Expression is IdentifierNameSyntax id &&
            id.Identifier.Text == "Type")
        {
            return true;
        }

        return false;
    }

    
    
    

    private string? GetContainingMemberSymbolId(SyntaxNode node, SemanticModel semanticModel)
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

    private string? MakeSymbolId(ISymbol symbol)
    {
        var docCommentId = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(docCommentId))
            return null;
        return $"{docCommentId}|{_assemblyIdentity}";
    }

    private static void CollectKnownNames(
        INamespaceSymbol ns,
        HashSet<string> typeNames,
        HashSet<string> memberNames)
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

    private (string? path, int? startLine, int? startColumn, int? endLine, int? endColumn)
        GetLocationInfo(Location location)
    {
        if (location == null || !location.IsInSource)
            return (null, null, null, null, null);

        var lineSpan = location.GetLineSpan();
        return (location.SourceTree?.FilePath,
                lineSpan.StartLinePosition.Line,
                lineSpan.StartLinePosition.Character,
                lineSpan.EndLinePosition.Line,
                lineSpan.EndLinePosition.Character);
    }

    private SemanticModel GetOrCreateSemanticModel(
        SyntaxTree syntaxTree,
        Dictionary<SyntaxTree, SemanticModel> cache)
    {
        if (!cache.TryGetValue(syntaxTree, out var model))
        {
            model = _compilation.GetSemanticModel(syntaxTree);
            cache[syntaxTree] = model;
        }
        return model;
    }
}
