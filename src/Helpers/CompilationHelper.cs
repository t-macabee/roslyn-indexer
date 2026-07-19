using Lurp.Storage;
using Microsoft.CodeAnalysis;
using System.Runtime.CompilerServices;

internal static class CompilationHelper
{
    public static async IAsyncEnumerable<(Project Project, Compilation Compilation)> GetAllAsync(Solution solution, [EnumeratorCancellation] CancellationToken cancellationToken = default)
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

    public static List<DiagnosticRecord> GetDiagnostics(string projectName, Compilation compilation)
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

            results.Add(new DiagnosticRecord
            {
                ProjectName = projectName,
                DocumentPath = documentPath,
                Severity = diag.Severity.ToString(),
                Id = diag.Id,
                Message = diag.GetMessage(),
                StartLine = startLine,
                StartColumn = startColumn,
                EndLine = endLine,
                EndColumn = endColumn,
            });
        }

        return results;
    }
}

