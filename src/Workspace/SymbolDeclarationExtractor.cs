using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Lurp.Storage;
using SymKind = Lurp.Storage.IndexedSymbolKind;

namespace Lurp.Workspace;

internal sealed class SymbolDeclarationExtractor(SymbolExtractionContext context)
{
    internal List<SymbolDeclaration> ExtractAll()
    {
        var results = new List<SymbolDeclaration>();

        foreach (var typeSymbol in SymbolExtractionContext.GetNamespaceTypeMembers(context.Compilation.Assembly.GlobalNamespace))
        {
            ExtractTypeDeclarations(typeSymbol, results);
        }

        return results;
    }

    private void ExtractTypeDeclarations(INamedTypeSymbol typeSymbol, List<SymbolDeclaration> results)
    {
        AddSymbolDeclarations(typeSymbol, results);

        foreach (var nestedType in typeSymbol.GetTypeMembers())
        {
            ExtractTypeDeclarations(nestedType, results);
        }

        foreach (var member in typeSymbol.GetMembers())
        {
            if (member is INamedTypeSymbol)
                continue;

            AddSymbolDeclarations(member, results);
        }
    }

    private void AddSymbolDeclarations(ISymbol symbol, List<SymbolDeclaration> results)
    {
        var docCommentId = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(docCommentId))
            return;

        var fqn = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var kind = MapKind(symbol);
        var metadataJson = BuildMetadataJson(symbol);

        var symbolId = new SymbolId(docCommentId, context.AssemblyIdentity, fqn);
        bool isPartial = symbol is INamedTypeSymbol typeSymbol && typeSymbol.DeclaringSyntaxReferences.Length > 1;

        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var syntaxNode = syntaxRef.GetSyntax();
            var syntaxTree = syntaxRef.SyntaxTree;

            var documentId = context.ResolveDocumentId(syntaxTree);
            if (documentId == null)
                continue;

            if (!context.DocumentVersions.TryGetValue(documentId.Value, out var versionId))
                continue;

            if (!context.DocumentContents.TryGetValue(documentId.Value, out var contentInfo))
                continue;

            var encoding = GetEncoding(contentInfo.Encoding);
            var sourceText = syntaxTree.GetText();
            var sourceString = sourceText.ToString();

            var (fullSpan, signatureSpan, bodySpan, nameSpan) = ComputeSpans(syntaxNode, sourceString, encoding);

            var isGenerated = context.GeneratedDocuments.Contains(documentId.Value);
            string? generatorIdentity = null;
            if (isGenerated && context.DocumentContents.TryGetValue(documentId.Value, out var genDocContent))
            {
                generatorIdentity = DeriveGeneratorIdentity(genDocContent.Content);
            }

            results.Add(new SymbolDeclaration
            {
                SymbolId = symbolId,
                Kind = kind,
                DocumentVersionId = versionId.ToString(),
                FullSpan = fullSpan,
                SignatureSpan = signatureSpan,
                BodySpan = bodySpan,
                NameSpan = nameSpan,
                IsPartial = isPartial,
                MetadataJson = metadataJson,
                IsGenerated = isGenerated,
                GeneratorIdentity = generatorIdentity,
            });
        }
    }

    // Computes four byte-offset spans for a declaration node:
    //   full      — entire syntax node (including trivia/braces)
    //   signature — from node start up to the body start (or node end if no body)
    //   body      — the body block (null if no body, e.g. abstract/property)
    //   name      — the identifier token span
    // All offsets are converted from char offsets to byte offsets using the document encoding.
    private static (DeclarationSpan full, DeclarationSpan signature, DeclarationSpan body, DeclarationSpan name)
        ComputeSpans(SyntaxNode node, string sourceText, Encoding encoding)
    {
        var fullCharSpan = node.FullSpan;
        var fullStart = CharOffsetToByteOffset(sourceText, fullCharSpan.Start, encoding);
        var fullEnd = CharOffsetToByteOffset(sourceText, fullCharSpan.End, encoding);
        var full = new DeclarationSpan(fullStart, fullEnd);

        var name = ComputeNameSpan(node, sourceText, encoding, full);
        var (body, signatureCharEnd) = ComputeBodyAndSignatureEnd(node, sourceText, encoding, fullCharSpan);
        var signature = new DeclarationSpan(fullStart, CharOffsetToByteOffset(sourceText, signatureCharEnd, encoding));

        return (full, signature, body, name);
    }

    private static DeclarationSpan ComputeNameSpan(SyntaxNode node, string sourceText, Encoding encoding, DeclarationSpan full)
    {
        SyntaxToken? GetIdentifier(SyntaxNode n) => n switch
        {
            BaseTypeDeclarationSyntax t => t.Identifier,
            MethodDeclarationSyntax m => m.Identifier,
            ConstructorDeclarationSyntax c => c.Identifier,
            PropertyDeclarationSyntax p => p.Identifier,
            EventDeclarationSyntax e => e.Identifier,
            VariableDeclaratorSyntax v => v.Identifier,
            EnumMemberDeclarationSyntax em => em.Identifier,
            _ => null
        };

        var idToken = GetIdentifier(node);
        if (idToken != null)
        {
            var idStart = CharOffsetToByteOffset(sourceText, idToken.Value.SpanStart, encoding);
            var idEnd = CharOffsetToByteOffset(sourceText, idToken.Value.Span.End, encoding);
            return new DeclarationSpan(idStart, idEnd);
        }

        var tokens = node.ChildTokens().Where(t => t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken) || t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GlobalKeyword)).ToArray();
        if (tokens.Length > 0)
        {
            var firstId = tokens[0];
            return new DeclarationSpan(CharOffsetToByteOffset(sourceText, firstId.SpanStart, encoding),
                CharOffsetToByteOffset(sourceText, firstId.Span.End, encoding));
        }

        return full;
    }

    private static (DeclarationSpan Body, int SignatureCharEnd) ComputeBodyAndSignatureEnd(SyntaxNode node, string sourceText, Encoding encoding, Microsoft.CodeAnalysis.Text.TextSpan fullCharSpan)
    {
        if (node is MethodDeclarationSyntax method && method.Body != null)
            return (SpanFromCharSpan(sourceText, method.Body.Span, encoding), method.Body.SpanStart);

        if (node is MethodDeclarationSyntax methodExpr && methodExpr.ExpressionBody != null)
            return (SpanFromCharSpan(sourceText, methodExpr.ExpressionBody.Span, encoding), methodExpr.ExpressionBody.SpanStart);

        if (node is MethodDeclarationSyntax && node is MethodDeclarationSyntax { Body: null, ExpressionBody: null })
            return (new DeclarationSpan(null, null), fullCharSpan.End);

        if (node is PropertyDeclarationSyntax prop && prop.AccessorList != null)
            return (new DeclarationSpan(null, null), fullCharSpan.End);

        if (node is PropertyDeclarationSyntax propExpr && propExpr.ExpressionBody != null)
            return (SpanFromCharSpan(sourceText, propExpr.ExpressionBody.Span, encoding), propExpr.ExpressionBody.SpanStart);

        if (node is BaseTypeDeclarationSyntax typeDecl)
        {
            var body = new DeclarationSpan(CharOffsetToByteOffset(sourceText, typeDecl.OpenBraceToken.SpanStart, encoding),
                CharOffsetToByteOffset(sourceText, typeDecl.CloseBraceToken.Span.End, encoding));
            return (body, typeDecl.OpenBraceToken.SpanStart);
        }

        if (node is EnumDeclarationSyntax enumDecl)
        {
            var body = new DeclarationSpan(CharOffsetToByteOffset(sourceText, enumDecl.OpenBraceToken.SpanStart, encoding),
                CharOffsetToByteOffset(sourceText, enumDecl.CloseBraceToken.Span.End, encoding));
            return (body, enumDecl.OpenBraceToken.SpanStart);
        }

        if (node is NamespaceDeclarationSyntax nsDecl)
        {
            var body = new DeclarationSpan(CharOffsetToByteOffset(sourceText, nsDecl.OpenBraceToken.SpanStart, encoding),
                CharOffsetToByteOffset(sourceText, nsDecl.CloseBraceToken.Span.End, encoding));
            return (body, nsDecl.OpenBraceToken.SpanStart);
        }

        return (new DeclarationSpan(null, null), fullCharSpan.End);
    }

    private static DeclarationSpan SpanFromCharSpan(string sourceText, Microsoft.CodeAnalysis.Text.TextSpan charSpan, Encoding encoding)
    {
        return new DeclarationSpan(CharOffsetToByteOffset(sourceText, charSpan.Start, encoding),
            CharOffsetToByteOffset(sourceText, charSpan.End, encoding));
    }

    private static int CharOffsetToByteOffset(string text, int charOffset, Encoding encoding)
    {
        if (charOffset <= 0)
            return 0;
        if (charOffset >= text.Length)
            return encoding.GetByteCount(text);

        return encoding.GetByteCount(text.AsSpan(0, charOffset));
    }

    private static Encoding GetEncoding(string encodingName)
    {
        return encodingName?.ToLowerInvariant() switch
        {
            "utf-8" => Encoding.UTF8,
            "utf-8-bom" => Encoding.UTF8,
            "utf-16-le" => Encoding.Unicode,
            "utf-16-be" => Encoding.BigEndianUnicode,
            _ => Encoding.UTF8,
        };
    }

    private static SymKind MapKind(ISymbol symbol)
    {
        return symbol.Kind switch
        {
            Microsoft.CodeAnalysis.SymbolKind.Namespace => SymKind.Namespace,
            Microsoft.CodeAnalysis.SymbolKind.NamedType => SymKind.Type,
            Microsoft.CodeAnalysis.SymbolKind.Method => SymKind.Method,
            Microsoft.CodeAnalysis.SymbolKind.Property => SymKind.Property,
            Microsoft.CodeAnalysis.SymbolKind.Field => SymKind.Field,
            Microsoft.CodeAnalysis.SymbolKind.Event => SymKind.Event,
            Microsoft.CodeAnalysis.SymbolKind.Parameter => SymKind.Parameter,
            Microsoft.CodeAnalysis.SymbolKind.Local => SymKind.Local,
            Microsoft.CodeAnalysis.SymbolKind.RangeVariable => SymKind.RangeVariable,
            Microsoft.CodeAnalysis.SymbolKind.ArrayType => SymKind.ArrayType,
            Microsoft.CodeAnalysis.SymbolKind.PointerType => SymKind.PointerType,
            Microsoft.CodeAnalysis.SymbolKind.TypeParameter => SymKind.TypeParameter,
            _ => SymKind.Unknown,
        };
    }

    private static string? BuildMetadataJson(ISymbol symbol)
    {
        var metadata = new Dictionary<string, object?>();

        if (symbol is IMethodSymbol method)
        {
            metadata["returnType"] = method.ReturnType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            metadata["isAbstract"] = method.IsAbstract;
            metadata["isVirtual"] = method.IsVirtual;
            metadata["isOverride"] = method.IsOverride;
            metadata["isStatic"] = method.IsStatic;
            metadata["isAsync"] = method.IsAsync;
            metadata["accessibility"] = method.DeclaredAccessibility.ToString();
            metadata["arity"] = method.Arity;
            metadata["isExtensionMethod"] = method.IsExtensionMethod;
        }
        else if (symbol is INamedTypeSymbol type)
        {
            metadata["typeKind"] = type.TypeKind.ToString();
            metadata["isAbstract"] = type.IsAbstract;
            metadata["isStatic"] = type.IsStatic;
            metadata["isRecord"] = type.IsRecord;
            metadata["accessibility"] = type.DeclaredAccessibility.ToString();
            metadata["arity"] = type.Arity;
        }
        else if (symbol is IPropertySymbol prop)
        {
            metadata["returnType"] = prop.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            metadata["isAbstract"] = prop.IsAbstract;
            metadata["isVirtual"] = prop.IsVirtual;
            metadata["isOverride"] = prop.IsOverride;
            metadata["isStatic"] = prop.IsStatic;
            metadata["isReadOnly"] = prop.IsReadOnly;
            metadata["isWriteOnly"] = prop.IsWriteOnly;
            metadata["accessibility"] = prop.DeclaredAccessibility.ToString();
        }
        else if (symbol is IFieldSymbol field)
        {
            metadata["returnType"] = field.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            metadata["isStatic"] = field.IsStatic;
            metadata["isReadOnly"] = field.IsReadOnly;
            metadata["isConst"] = field.IsConst;
            metadata["isVolatile"] = field.IsVolatile;
            metadata["accessibility"] = field.DeclaredAccessibility.ToString();
        }
        else if (symbol is IEventSymbol evt)
        {
            metadata["returnType"] = evt.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            metadata["isAbstract"] = evt.IsAbstract;
            metadata["isVirtual"] = evt.IsVirtual;
            metadata["isOverride"] = evt.IsOverride;
            metadata["isStatic"] = evt.IsStatic;
            metadata["accessibility"] = evt.DeclaredAccessibility.ToString();
        }

        return metadata.Count > 0
            ? System.Text.Json.JsonSerializer.Serialize(metadata)
            : null;
    }

    private static string? DeriveGeneratorIdentity(byte[] content)
    {
        if (content.Length < 512)
            return null;

        var headerText = Encoding.UTF8.GetString(content, 0, 512);

        var generatedCodeAttr = "[GeneratedCode(";
        var attrIndex = headerText.IndexOf(generatedCodeAttr, StringComparison.OrdinalIgnoreCase);
        if (attrIndex >= 0)
        {
            var start = attrIndex + generatedCodeAttr.Length;
            var end = headerText.IndexOf('"', start + 1);
            if (end > start)
            {
                return headerText[(start + 1)..end];
            }
        }

        if (headerText.Contains("<auto-generated>", StringComparison.OrdinalIgnoreCase))
        {
            var autoGenIndex = headerText.IndexOf("<auto-generated>", StringComparison.OrdinalIgnoreCase);
            var afterTag = headerText[(autoGenIndex + "<auto-generated>".Length)..].TrimStart();

            var toolName = new string(afterTag.TakeWhile(c => c != '/' && c != '\n' && c != '\r' && c != '>').ToArray()).Trim();
            if (!string.IsNullOrEmpty(toolName))
                return toolName;

            return "auto-generated-header";
        }

        return null;
    }
}
