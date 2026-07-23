# Sample Fixture Solution

Committed fixture for integration tests (T19, T21-R). Not compiled as part of the test assembly — opened only through `MSBuildWorkspace`.

## Structure

| Project | Contents | Exercises |
|---|---|---|
| Library | Widget (partial), IShape/Circle/Square, Repository<T>/UserRepository, stand-in base types | Cross-project type refs (D1), partial classes, polymorphism, generics |
| Contracts | MediatR request/notification types | Cross-project Handles edges (MediatR adapter) |
| App | Positional records, DbContext, Controller, MediatR handler | C1, EF Core adapter, ASP.NET adapter, cross-project Handles |
| Sample.Tests | One xUnit [Fact] calling App production code | TestAdapter TestedBy cross-assembly edges |

## Adapter strategy

- **MediatR**: real NuGet package (adapter checks `ReferencedAssemblyNames` for "MediatR").
- **ASP.NET Core / EF Core**: local stand-in base types (`Controller`, `DbContext`) — adapters match by short name only.
