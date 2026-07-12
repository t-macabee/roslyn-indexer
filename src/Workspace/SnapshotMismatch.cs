namespace RoslynIndexer;





public enum MismatchKind
{
    
    DocumentAdded,

    
    DocumentRemoved,

    
    DocumentModified,

    
    SdkChanged,

    
    CompilerChanged,

    
    TargetFrameworkChanged,

    
    ProjectAdded,

    
    ProjectRemoved,

    
    ProjectReferenceChanged,

    
    VersionChanged,
}















public sealed record SnapshotMismatch(
    MismatchKind Kind,
    string Description,
    DocumentId? Document,
    string? Detail);
