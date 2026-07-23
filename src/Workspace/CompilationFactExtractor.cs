using Lurp.Adapters;
using Lurp.Storage;
using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

public static class CompilationFactExtractor
{
    public sealed record ExtractionResult(List<SymbolDeclaration> Declarations, List<EdgeRecord> Edges, List<DiagnosticRecord> Diagnostics, int SkippedDeclarations = 0);

    public static ExtractionResult ExtractAll(Compilation compilation, WorkspaceInfo workspaceInfo, string snapshotId, string projectName, IReadOnlySet<string>? skipAdapters = null, Action<string>? logWarning = null, Action<string>? logError = null, IReadOnlySet<string>? scopeDocuments = null)
    {
        var symbolExtractor = new SymbolExtractor(compilation, workspaceInfo.DocumentContents, workspaceInfo.Documents, workspaceInfo.GeneratedDocuments, snapshotId, logWarning, scopeDocuments);

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

        var memberEdgeExtractor = new MemberEdgeExtractor(compilation, workspaceInfo.Documents, workspaceInfo.GeneratedDocuments, snapshotId, gitRoot, scopeDocuments);

        try
        {
            edges.AddRange(memberEdgeExtractor.ExtractAll());
        }
        catch (Exception ex)
        {
            logError?.Invoke($"Member edge extraction failed for project '{projectName}': {ex.Message}");
        }


        var polyExtractor = new PolymorphismExtractor(compilation, snapshotId, gitRoot, scopeDocuments);

        edges.AddRange(polyExtractor.ExtractAll());

        try
        {
            var reflectionExtractor = new ReflectionExtractor(compilation, snapshotId, gitRoot, scopeDocuments);
            edges.AddRange(reflectionExtractor.Extract());
        }
        catch (Exception ex)
        {
            logWarning?.Invoke($"Reflection extraction failed: {ex.Message}");
        }

        var adapters = AdapterRegistry.GetAdapters(skipAdapters);

        var locationResolver = new EdgeLocationResolver(workspaceInfo.Documents, workspaceInfo.GeneratedDocuments, gitRoot);

        foreach (var adapter in adapters)
        {
            try
            {
                edges.AddRange(adapter.Extract(compilation, snapshotId, locationResolver));
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
