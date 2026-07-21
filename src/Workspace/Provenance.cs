namespace Lurp.Workspace;

/// <summary>
/// Canonical evidence-grade vocabulary for edges.
///
/// Resolution of roadmap ambiguity #1:
///   The canonical name for string-based reflection matches is "name_candidate"
///   (not "string_candidate"). This was chosen because the reflection extractors
///   match by symbol name, not arbitrary string content.
/// </summary>
public static class Provenance
{
    /// <summary>The fact was proven by the compiler itself (e.g. type resolution, method dispatch).</summary>
    public const string CompilerProved = "compiler_proved";

    /// <summary>The fact was derived from a framework convention or adapter (e.g. ASP.NET routing).</summary>
    public const string FrameworkDerived = "framework_derived";

    /// <summary>The fact is a valid but unconfirmed candidate (e.g. inherited implementation).</summary>
    public const string Possible = "possible";

    /// <summary>The fact was inferred by matching a string literal against symbol names.</summary>
    public const string NameCandidate = "name_candidate";

    /// <summary>The fact cannot be statically determined and requires runtime verification.</summary>
    public const string RuntimeUnknown = "runtime_unknown";

    /// <summary>Framework convention-based inference (e.g. ASP.NET controller naming).</summary>
    public const string Convention = "convention";

    /// <summary>All canonical provenance values.</summary>
    public static readonly IReadOnlySet<string> CanonicalValues = new HashSet<string>(StringComparer.Ordinal)
    {
        CompilerProved,
        FrameworkDerived,
        Possible,
        NameCandidate,
        RuntimeUnknown,
        Convention,
    };

    /// <summary>
    /// Normalize a provenance string to a canonical value.
    /// Unknown values are returned as-is (reads are survivable per roadmap §5.6).
    /// </summary>
    public static string Normalize(string provenance)
    {
        if (string.IsNullOrEmpty(provenance))
            return string.Empty;

        // Strip legacy composite suffix
        if (provenance.EndsWith(":cross_generated", StringComparison.Ordinal))
        {
            provenance = provenance[..^":cross_generated".Length];
        }

        // Map legacy values to canonical constants
        return provenance switch
        {
            "roslyn" => CompilerProved,
            _ => CanonicalValues.Contains(provenance) ? provenance : provenance,
        };
    }
}
