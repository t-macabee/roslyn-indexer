using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

internal static class CompilationHelper
{
    public static async IAsyncEnumerable<(Project Project, Compilation Compilation)> GetAllAsync(
        Solution solution,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken)
                .ConfigureAwait(false);
            if (compilation != null)
                yield return (project, compilation);
        }
    }
}
