using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Lurp.Storage;

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

    
    
    
    public static List<DiagnosticRecord> GetDiagnostics(
        string projectName,
        Compilation compilation)
    {
        var results = new List<DiagnosticRecord>();

        var diagnostics = compilation.GetDiagnostics();
        foreach (var diag in diagnostics)
        {
            var loc = diag.Location;
            int? startLine = null, startColumn = null, endLine = null, endColumn = null;
            string? documentPath = null;

            if (loc.IsInSource && loc.SourceTree != null)
            {
                var span = loc.GetLineSpan();
                documentPath = loc.SourceTree.FilePath;
                startLine = span.StartLinePosition.Line;
                startColumn = span.StartLinePosition.Character;
                endLine = span.EndLinePosition.Line;
                endColumn = span.EndLinePosition.Character;
            }

            results.Add(new DiagnosticRecord(
                projectName: projectName,
                documentPath: documentPath,
                severity: diag.Severity.ToString(),
                id: diag.Id,
                message: diag.GetMessage(),
                startLine: startLine,
                startColumn: startColumn,
                endLine: endLine,
                endColumn: endColumn));
        }

        return results;
    }
}

