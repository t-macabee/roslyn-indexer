namespace Lurp.Workspace;

/// <summary>
/// Single source of truth for every extractor whose version string appears
/// in <c>edges.extractor_version</c>.  The registry is built from
/// <see cref="ExtractorConstants"/> + <see cref="VersionConstants"/> +
/// adapter versions so that the <c>extractors</c> table can be populated
/// idempotently by both full and incremental index runs.
/// </summary>
internal static class ExtractorRegistry
{
    /// <summary>(Name, Version, Description) for every known extractor.</summary>
    /// <remarks>
    /// <c>Version</c> is the exact string written into
    /// <c>edges.extractor_version</c>.  <c>Name</c> is a short human-readable
    /// identifier for the extractor.
    /// </remarks>
    internal static IReadOnlyList<(string Name, string Version, string Description)> All { get; } =
        new (string Name, string Version, string Description)[]
        {
            // -- Member-edge extractors (ExtractorConstants) --
            ("Declares",              ExtractorConstants.DeclaresExtractor,              "Type-declares-member containment edges"),
            ("Calls",                 ExtractorConstants.CallsExtractor,                 "Direct method/function call edges"),
            ("Constructs",            ExtractorConstants.ConstructsExtractor,            "Object-construction (new) edges"),
            ("Overrides",             ExtractorConstants.OverridesExtractor,             "Method override edges"),
            ("ReadsWrites",           ExtractorConstants.ReadsWritesExtractor,           "Field/property read and write edges"),
            ("Returns",               ExtractorConstants.ReturnsExtractor,               "Return-type reference edges"),
            ("Throws",                ExtractorConstants.ThrowsExtractor,                "Thrown-exception type edges"),
            ("ParameterDependencies", ExtractorConstants.ParameterDependenciesExtractor, "Parameter-type dependency edges"),

            // -- Reflection extractors --
            ("Reflection",            ExtractorConstants.ReflectionExtractor,            "Reflection-based dependency edges (nameof/typeof/string-literal)"),

            // -- Static-dispatch extractor --
            ("StaticDispatch",        ExtractorConstants.StaticallyCallsExtractor,       "Static (non-virtual) dispatch call edges"),

            // -- Polymorphism extractor --
            ("Polymorphism",          ExtractorConstants.PolymorphismExtractor,          "Polymorphic (virtual) dispatch edges"),

            // -- Structural type-relationship extractor (uses VersionConstants.ExtractorVersion) --
            ("Structural",            VersionConstants.ExtractorVersion,                 "Structural type edges (inherits, implements, contains, references)"),

            // -- Framework adapters --
            ("AspNetCore",            "aspnetcore-v1",     "ASP.NET Core framework edges (controller actions, middleware)"),
            ("MediatR",               "mediatr-v1",        "MediatR framework edges (request/handler)"),
            ("EfCore",                "efcore-v1",         "Entity Framework Core edges (DbSets, entity mappings)"),
            ("Serialization",         "serialization-v1",  "Serialization framework edges (JSON/XML contracts)"),
            ("DependencyInjection",   ExtractorConstants.DependencyInjectionExtractor, "Dependency injection container edges"),

            // -- Test adapter (hard-coded in TestAdapter.cs:148) --
            ("Test",                  "test-v1",           "Test-to-production-code tested-by edges"),
        };
}
