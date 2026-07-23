namespace Library;

/// <summary>Stand-in base types for ASP.NET Core and EF Core adapter detection.
/// The adapters match by short name on the base-type chain, so these empty
/// classes are sufficient to trigger controller and DbContext edges without
/// pulling in the real framework packages.</summary>
public class Controller { }

public class ControllerBase { }

public class DbContext { }
