using System.Globalization;
using System.IO.Hashing;  // Not used directly here (SHA-256 lives in System.Security.Cryptography),
                           // but retained to acknowledge the project dependency.
using System.Security.Cryptography;
using System.Text;

namespace RoslynIndexer;

/// <summary>
/// Uniquely identifies a workspace: the combination of a Git repository root
/// and the solution path within (or outside) that root.
/// </summary>
public readonly record struct WorkspaceId
{
    /// <summary>
    /// Normalized absolute Git root path (forward slashes, no trailing slash).
    /// </summary>
    public string GitRoot { get; }

    /// <summary>
    /// Normalized absolute solution path (forward slashes).
    /// </summary>
    public string SolutionPath { get; }

    /// <summary>
    /// Deterministic composite string: normalised form suitable for use as a key.
    /// Format: <c>workspace://{gitRoot}/{relativeSolutionPath}</c>
    /// </summary>
    public string Value { get; }

    private WorkspaceId(string gitRoot, string solutionPath, string value)
    {
        GitRoot = gitRoot;
        SolutionPath = solutionPath;
        Value = value;
    }

    /// <summary>
    /// Creates a <see cref="WorkspaceId"/> from a raw Git root and solution path.
    /// Both paths are normalised (forward slashes, trimmed trailing separators).
    /// The <see cref="Value"/> embeds the solution path relative to the Git root
    /// when possible, falling back to the absolute solution path otherwise.
    /// </summary>
    public static WorkspaceId Create(string gitRoot, string solutionPath)
    {
        var root = Normalise(gitRoot).TrimEnd('/');
        var sln  = Normalise(solutionPath);

        var relative = sln.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            ? sln[(root.Length + 1)..]                     // keep it relative
            : sln;                                          // outside git root — use absolute

        var value = $"workspace://{root}/{relative}";
        return new WorkspaceId(root, sln, value);
    }

    public override string ToString() => Value;

    private static string Normalise(string path)
        => Path.GetFullPath(path).Replace('\\', '/');
}

/// <summary>
/// Uniquely identifies one indexed snapshot of a workspace.
/// Implemented as a GUID for simplicity and global uniqueness.
/// </summary>
public readonly record struct SnapshotId
{
    /// <summary>The backing GUID.</summary>
    public Guid Value { get; }

    /// <summary>Creates a snapshot ID from an existing GUID.</summary>
    public SnapshotId(Guid value) => Value = value;

    /// <summary>Creates a snapshot ID from a hex/string GUID representation.</summary>
    /// <exception cref="FormatException">Thrown when <paramref name="value"/> is not a valid GUID.</exception>
    public static SnapshotId Parse(string value) => new(Guid.Parse(value, CultureInfo.InvariantCulture));

    /// <summary>Creates a new, unique snapshot ID.</summary>
    public static SnapshotId New() => new(Guid.NewGuid());

    /// <summary>
    /// Returns the GUID as a 32-digit lowercase hex string (no hyphens).
    /// This representation is stable and safe for file-system paths.
    /// </summary>
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>
/// Stable logical identity for a document within a workspace.
/// The identity is the file's path relative to the Git root, normalised to forward slashes.
/// </summary>
public readonly record struct DocumentId
{
    /// <summary>
    /// Relative path from the Git root to the file, using forward-slash separators.
    /// Example: <c>src/MyProject/MyFile.cs</c>
    /// </summary>
    public string RelativePath { get; }

    /// <summary>
    /// Creates a <see cref="DocumentId"/> from a relative path.
    /// All separator characters are normalised to forward slashes.
    /// </summary>
    public DocumentId(string relativePath)
    {
        RelativePath = (relativePath ?? "").Replace('\\', '/');
    }

    public override string ToString() => RelativePath;
}

/// <summary>
/// Immutable content-version identifier for a document.
/// Wraps a SHA-256 content hash as a lowercase hex string.
/// </summary>
public readonly record struct DocumentVersionId
{
    /// <summary>The SHA-256 hash as a lowercase hex string (64 characters).</summary>
    public string Hash { get; }

    /// <summary>Wraps an already-computed SHA-256 hex string.</summary>
    public DocumentVersionId(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        Hash = hash;
    }

    // -- Factory methods ------------------------------------------------

    /// <summary>Computes the SHA-256 hash of a byte array.</summary>
    public static DocumentVersionId Compute(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return new DocumentVersionId(Hex(hash));
    }

    /// <summary>Computes the SHA-256 hash of a UTF-8 string.</summary>
    public static DocumentVersionId Compute(string text)
        => Compute(Encoding.UTF8.GetBytes(text));

    /// <summary>Computes the SHA-256 hash of a stream.</summary>
    public static DocumentVersionId Compute(Stream stream)
    {
        var hash = SHA256.HashData(stream);
        return new DocumentVersionId(Hex(hash));
    }

    public override string ToString() => Hash;

    // -- Helpers --------------------------------------------------------

    private static string Hex(byte[] bytes)
        => Convert.ToHexStringLower(bytes);
}
