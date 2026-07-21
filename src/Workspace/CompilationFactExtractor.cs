using Lurp.Adapters;
using Lurp.Storage;
using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

public static class CompilationFactExtractor
{
    public sealed record ExtractionResult(List<SymbolDeclaration> Declarations, List<EdgeRecord> Edges, List<DiagnosticRecord> Diagnostics, int SkippedDeclarations = 0);

    public static ExtractionResult ExtractAll(Compilation compilation, WorkspaceInfo workspaceInfo, string snapshotId, string projectName, IReadOnlySet<string>? skipAdapters = null, Action<string>? logWarning = null, Action<string>? logError = null)
    {
        var symbolExtractor = new SymbolExtractor(compilation, workspaceInfo.DocumentContents, workspaceInfo.Documents, workspaceInfo.GeneratedDocuments, snapshotId, logWarning);

        List<SymbolDeclaration> declarations;
        int skippedDeclarations = 0;
        try
        {
            var result = symbolExtractor.ExtractAll();
            declarations = result.Declarations;
            skippedDeclarations = result.SkippedCount;
        }
        catch (Exception ex)
        {
            logError?.Invoke($"Symbol extraction failed for project '{projectName}': {ex.Message}");
            declarations = new List<SymbolDeclaration>();
        }

        List<EdgeRecord> edges;
        try
        {
            edges = symbolExtractor.ExtractEdges();
        }
        catch (Exception ex)
        {
            logError?.Invoke($"Edge extraction failed for project '{projectName}': {ex.Message}");
            edges = new List<EdgeRecord>();
        }


        var gitRoot = workspaceInfo.Id.GitRoot;

        var memberEdgeExtractor = new MemberEdgeExtractor(compilation, workspaceInfo.Documents, workspaceInfo.GeneratedDocuments, snapshotId, gitRoot);

        try
        {
            edges.AddRange(memberEdgeExtractor.ExtractAll());
        }
        catch (Exception ex)
        {
            logError?.Invoke($"Member edge extraction failed for project '{projectName}': {ex.Message}");
        }


        var polyExtractor = new PolymorphismExtractor(compilation, snapshotId, gitRoot);

        edges.AddRange(polyExtractor.ExtractAll());

        try
        {
            var reflectionExtractor = new ReflectionExtractor(compilation, snapshotId, gitRoot);
            edges.AddRange(reflectionExtractor.Extract());
        }
        catch (Exception ex)
        {
            logWarning?.Invoke($"Reflection extraction failed: {ex.Message}");
        }

        var adapters = AdapterRegistry.GetAdapters(skipAdapters);

        foreach (var adapter in adapters)
        {
            try
            {
                edges.AddRange(adapter.Extract(compilation, snapshotId));
            }
            catch (Exception ex)
            {
                logError?.Invoke($"Adapter '{adapter.Name}' failed: {ex.Message}");
            }
        }

        var diagnostics = CompilationHelper.GetDiagnostics(projectName, compilation);

        return new ExtractionResult(declarations, edges, diagnostics, skippedDeclarations);
    }
}
