using System.Globalization;

using System.Security.Cryptography;
using System.Text;

namespace Lurp.Workspace;

public readonly record struct WorkspaceId
{

    public string GitRoot { get; }

    public string SolutionPath { get; }

    public string Value { get; }

    private WorkspaceId(string gitRoot, string solutionPath, string value)
    {
        GitRoot = gitRoot;
        SolutionPath = solutionPath;
        Value = value;
    }

    public static WorkspaceId Create(string gitRoot, string solutionPath)
    {
        var root = Normalise(gitRoot).TrimEnd('/');
        var sln = Normalise(solutionPath);

        var relative = sln.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            ? sln[(root.Length + 1)..]
            : sln;

        var value = $"workspace://{root}/{relative}";
        return new WorkspaceId(root, sln, value);
    }

    public override string ToString() => Value;

    private static string Normalise(string path)
        => Path.GetFullPath(path).Replace('\\', '/');
}

public readonly record struct SnapshotId
{

    public Guid Value { get; }

    public SnapshotId(Guid value) => Value = value;

    public static SnapshotId Parse(string value) => new(Guid.Parse(value, CultureInfo.InvariantCulture));

    public static SnapshotId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

public readonly record struct DocumentId
{

    public string RelativePath { get; }

    public DocumentId(string relativePath)
    {
        RelativePath = (relativePath ?? "").Replace('\\', '/');
    }

    public override string ToString() => RelativePath;
}

public readonly record struct DocumentVersionId
{

    public string Hash { get; }

    public DocumentVersionId(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        Hash = hash;
    }

    public static DocumentVersionId Compute(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return new DocumentVersionId(Hex(hash));
    }

    public static DocumentVersionId Compute(string text)
        => Compute(Encoding.UTF8.GetBytes(text));

    public static DocumentVersionId Compute(Stream stream)
    {
        var hash = SHA256.HashData(stream);
        return new DocumentVersionId(Hex(hash));
    }

    public override string ToString() => Hash;

    private static string Hex(byte[] bytes)
        => Convert.ToHexStringLower(bytes);
}

