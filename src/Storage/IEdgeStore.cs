using System.Collections.Generic;

namespace Lurp.Storage
{
    public interface IEdgeStore
    {
        void SaveEdges(string snapshotId, IEnumerable<EdgeRecord> edges);
        void SaveDiagnostics(string snapshotId, IEnumerable<DiagnosticRecord> diagnostics);
        void SaveAnnotations(string snapshotId, IEnumerable<AnnotationRecord> annotations);

        List<EdgeRecord> GetEdges(string snapshotId, string? symbolId = null);
        List<DiagnosticRecord> GetDiagnostics(string snapshotId, string? projectName = null);
        List<AnnotationRecord> GetAnnotations(string snapshotId, string? symbolId = null);

        List<EdgeRecord> GetEdgesByKind(string snapshotId, string kind);
        List<EdgeRecord> GetIncomingEdges(string snapshotId, string symbolId);
        List<EdgeRecord> GetOutgoingEdges(string snapshotId, string symbolId);

        void DeleteEdgesByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths);
        void DeleteEdgesWithNullDocumentPathForAssemblies(string snapshotId, IEnumerable<string> assemblyIdentities);
        void CopyEdgesToSnapshot(string fromSnapshotId, string toSnapshotId);

        void CopySnapshotDiagnostics(string fromSnapshotId, string toSnapshotId);
        void DeleteDiagnosticsByProjectNames(string snapshotId, IEnumerable<string> projectNames);
    }
}
