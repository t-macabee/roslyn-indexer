namespace Lurp.Workspace;

internal sealed class GitIgnoreMatcher
{
    private readonly List<GitIgnorePattern> _patterns;

    private GitIgnoreMatcher(List<GitIgnorePattern> patterns)
    {
        _patterns = patterns;
    }

    public static GitIgnoreMatcher Load(string gitRoot)
    {
        var gitignorePath = Path.Combine(gitRoot, ".gitignore");
        if (!File.Exists(gitignorePath))
            return new GitIgnoreMatcher([]);

        var patterns = new List<GitIgnorePattern>();
        foreach (var line in File.ReadLines(gitignorePath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var negated = false;
            if (trimmed.StartsWith('!'))
            {
                negated = true;
                trimmed = trimmed[1..];
            }

            var isDirectoryPattern = trimmed.EndsWith('/');
            if (isDirectoryPattern)
                trimmed = trimmed.TrimEnd('/');

            patterns.Add(new GitIgnorePattern(trimmed, negated, isDirectoryPattern));
        }

        return new GitIgnoreMatcher(patterns);
    }

    public bool IsIgnored(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        bool ignored = false;

        foreach (var pattern in _patterns)
        {
            if (Matches(normalized, pattern))
                ignored = !pattern.Negated;
        }

        return ignored;
    }

    private static bool Matches(string path, GitIgnorePattern pattern)
    {
        return SimpleGlobMatch(path, pattern.Pattern, pattern.IsDirectoryPattern);
    }

    private static bool SimpleGlobMatch(string path, string pattern, bool isDirectoryPattern)
    {
        // Handle simple glob patterns: **, *, ?
        // This is a simplified matcher that covers common .gitignore patterns:
        // - Exact match: "bin/" matches "bin/"
        // - Wildcard: "*.log" matches any .log file
        // - Double-star: "**/obj/" matches any nested obj/ directory
        // - Leading slash: "/src/foo.cs" matches from root

        if (pattern.Contains("**"))
        {
            var parts = pattern.Split("**");
            string remaining = path;

            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i].Trim('/');

                if (i == 0)
                {
                    if (part.Length > 0 && !remaining.StartsWith(part, StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (part.Length > 0)
                        remaining = remaining[part.Length..];
                }
                else if (i == parts.Length - 1)
                {
                    if (part.Length > 0 && !remaining.EndsWith(part, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                else
                {
                    var idx = remaining.IndexOf(part, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0)
                        return false;
                    remaining = remaining[(idx + part.Length)..];
                }
            }

            return true;
        }

        if (pattern.Contains('*') || pattern.Contains('?'))
        {
            return WildcardMatch(path, pattern);
        }

        if (isDirectoryPattern)
        {
            return path.StartsWith(pattern, StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("/" + pattern, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(path, pattern, StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("/" + pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool WildcardMatch(string input, string pattern)
    {
        int i = 0, p = 0;
        int starIdx = -1, matchIdx = -1;

        while (i < input.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' ||
                char.ToUpperInvariant(input[i]) == char.ToUpperInvariant(pattern[p])))
            {
                i++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starIdx = p;
                matchIdx = i;
                p++;
            }
            else if (starIdx != -1)
            {
                p = starIdx + 1;
                matchIdx++;
                i = matchIdx;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;

        return p == pattern.Length;
    }

    private sealed record GitIgnorePattern(string Pattern, bool Negated, bool IsDirectoryPattern);
}
