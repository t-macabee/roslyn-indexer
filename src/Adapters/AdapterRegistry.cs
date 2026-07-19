namespace Lurp.Adapters;

internal static class AdapterRegistry
{
    private static readonly IFrameworkAdapter[] All =
    [
        new AspNetCoreAdapter(),
        new DependencyInjectionAdapter(),
        new MediatRAdapter(),
        new EfCoreAdapter(),
        new SerializationAdapter(),
        new TestAdapter(),
    ];

    public static IFrameworkAdapter[] GetAdapters(IReadOnlySet<string>? skipAdapters = null)
    {
        if (skipAdapters == null || skipAdapters.Count == 0)
            return All;
        return [.. All.Where(a => !skipAdapters.Contains(a.Name, StringComparer.OrdinalIgnoreCase))];
    }
}
