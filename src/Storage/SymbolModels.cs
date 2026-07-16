using System;
using System.Text.Json;

namespace Lurp.Storage
{
    public enum EdgeKind
    {
        Inherits,
        Implements,
        References,
        Contains,
        Overrides,
        Calls,
        Constructs,
        Reads,
        Writes,
        Returns,
        Throws,
        Declares,
        MayDispatchTo,
        RoutesTo,
        Registers,
        Handles,
        MapsTo,
        TestedBy,
        ReflectionTypeRef,
        ReflectionMemberRef,
        ReflectionNameCandidate,
        ReflectionTargetUnknown,
    }
}

namespace Lurp.Storage
{
    public enum SymbolKind
    {
        Namespace,
        Type,
        Method,
        Property,
        Field,
        Event,
        Parameter,
        Local,
        RangeVariable,
        NamedType,
        ArrayType,
        PointerType,
        TypeParameter,
        Unknown
    }

    public sealed class SymbolId : IEquatable<SymbolId>
    {
        public string Value { get; }
        public string DocCommentId { get; }
        public string AssemblyIdentity { get; }
        public string? FullyQualifiedName { get; }

        public SymbolId(string docCommentId, string assemblyIdentity, string? fullyQualifiedName = null)
        {
            DocCommentId = docCommentId ?? throw new ArgumentNullException(nameof(docCommentId));
            AssemblyIdentity = assemblyIdentity ?? throw new ArgumentNullException(nameof(assemblyIdentity));
            FullyQualifiedName = fullyQualifiedName;
            Value = $"{docCommentId}|{assemblyIdentity}";
        }

        public bool Equals(SymbolId? other) =>
            other is not null && Value == other.Value;

        public override bool Equals(object? obj) =>
            obj is SymbolId other && Equals(other);

        public override int GetHashCode() =>
            StringComparer.Ordinal.GetHashCode(Value);

        public override string ToString() => Value;

        public static SymbolId Parse(string value)
        {
            var pipeIndex = value.IndexOf('|');
            if (pipeIndex < 0)
                throw new FormatException($"Invalid SymbolId format: '{value}'. Expected 'docCommentId|assemblyIdentity'.");
            return new SymbolId(
                docCommentId: value[..pipeIndex],
                assemblyIdentity: value[(pipeIndex + 1)..]);
        }
    }

    public sealed class DeclarationSpan
    {
        public int? Start { get; }
        public int? End { get; }
        public int Length => Start.HasValue && End.HasValue ? End.Value - Start.Value : 0;

        public DeclarationSpan(int? start, int? end)
        {
            if (start.HasValue && end.HasValue && start.Value > end.Value)
                throw new ArgumentException($"Start ({start}) must be <= End ({end}).");
            Start = start;
            End = end;
        }

        public byte[]? Slice(byte[] source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (!Start.HasValue || !End.HasValue)
                return null;
            if (Start.Value < 0 || End.Value > source.Length)
                return null;
            var length = End.Value - Start.Value;
            if (length == 0)
                return Array.Empty<byte>();
            var result = new byte[length];
            Array.Copy(source, Start.Value, result, 0, length);
            return result;
        }

        public string? SliceToString(byte[] source)
        {
            var sliced = Slice(source);
            return sliced != null ? System.Text.Encoding.UTF8.GetString(sliced) : null;
        }

        public bool IsEmpty => !Start.HasValue || !End.HasValue || Start.Value == End.Value;

        public override string ToString() =>
            Start.HasValue && End.HasValue
                ? $"[{Start}..{End}) ({Length} bytes)"
                : "(null)";
    }

    public sealed class SymbolDeclaration
    {
        public SymbolId SymbolId { get; }
        public SymbolKind Kind { get; }
        public string DocumentVersionId { get; }
        public DeclarationSpan FullSpan { get; }
        public DeclarationSpan SignatureSpan { get; }
        public DeclarationSpan BodySpan { get; }
        public DeclarationSpan NameSpan { get; }
        public bool IsPartial { get; }
        public string? MetadataJson { get; }

        public SymbolDeclaration(
            SymbolId symbolId,
            SymbolKind kind,
            string documentVersionId,
            DeclarationSpan fullSpan,
            DeclarationSpan signatureSpan,
            DeclarationSpan bodySpan,
            DeclarationSpan nameSpan,
            bool isPartial = false,
            string? metadataJson = null)
        {
            SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
            Kind = kind;
            DocumentVersionId = documentVersionId ?? throw new ArgumentNullException(nameof(documentVersionId));
            FullSpan = fullSpan ?? throw new ArgumentNullException(nameof(fullSpan));
            SignatureSpan = signatureSpan ?? throw new ArgumentNullException(nameof(signatureSpan));
            BodySpan = bodySpan ?? throw new ArgumentNullException(nameof(bodySpan));
            NameSpan = nameSpan ?? throw new ArgumentNullException(nameof(nameSpan));
            IsPartial = isPartial;
            MetadataJson = metadataJson;
        }
    }
}

