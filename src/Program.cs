using System;
using System.Collections.Generic;
using System.Linq;
using Lurp.Handlers;

namespace Lurp;

public class Program
{
    private static readonly Dictionary<string, Action<string[]>> ModeHandlers = new(StringComparer.Ordinal)
    {
        ["get-source"] = GetSourceHandler.Run,
        ["get-symbol"] = GetSymbolHandler.Run,
        ["search"] = SearchHandler.Run,
        ["find-symbol"] = FindSymbolHandler.Run,
        ["index"] = a => IndexHandler.Run(a).GetAwaiter().GetResult(),
        ["diff"] = DiffHandler.Run,
        ["impact"] = ImpactHandler.Run,
        ["context"] = ContextHandler.Run,
        ["status"] = a => StatusHandler.Run(a).GetAwaiter().GetResult(),
        ["simulate-rename"] = SimulateRenameHandler.Run,
        ["simulate-move"] = SimulateMoveHandler.Run,
        ["simulate-remove"] = SimulateRemoveHandler.Run,
        ["audit"] = AuditHandler.Run,
        ["timings"] = TimingsHandler.Run,
        ["annotate"] = AnnotationHandler.RunAnnotate,
        ["get-annotations"] = AnnotationHandler.RunGetAnnotations,
    };

    public static void Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h") || args.Contains("--mode=help") || args.Length == 0)
        {
            PrintHelp();
            return;
        }

        var modeArg = args.FirstOrDefault(a => a.StartsWith("--mode="));
        if (modeArg is null)
        {
            PrintUnknownModeError();
            Environment.Exit(1);
            return;
        }

        var mode = modeArg["--mode=".Length..];
        if (ModeHandlers.TryGetValue(mode, out var handler))
        {
            handler(args);
        }
        else
        {
            PrintUnknownModeError();
            Environment.Exit(1);
        }
    }

    private static void PrintUnknownModeError()
    {
        Console.Error.WriteLine("ERROR: Unknown mode. Use --mode=index, --mode=get-source, --mode=get-symbol, --mode=search, --mode=find-symbol, --mode=diff, --mode=impact, --mode=context, --mode=status, --mode=timings, --mode=simulate-rename, --mode=simulate-move, --mode=simulate-remove, --mode=audit, --mode=annotate, or --mode=get-annotations.");
        Console.Error.WriteLine("  Note: For --mode=index, use --strategy=<incremental|full> (default: full on first run, incremental on subsequent runs).");
        Console.Error.WriteLine("    --strategy=full forces a complete reindex. Use it as a recovery mechanism if something looks wrong.");
        Console.Error.WriteLine("  Note: 'structure' is served by --mode=context --intent=inspect.");
        Console.Error.WriteLine("  Note: 'who-references' is served by --mode=impact --direction=upstream.");
        Console.Error.WriteLine("  Note: 'discover' is served by --mode=search --type=symbol --kind=<TypeKind>.");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("lurp — Roslyn-based code indexer");
        Console.WriteLine();
        Console.WriteLine("MODES");
        Console.WriteLine("  --mode=index              Index a solution and store facts in the database.");
        Console.WriteLine("  --mode=get-source          Retrieve source for a symbol by ID.");
        Console.WriteLine("  --mode=get-symbol          Look up symbol metadata.");
        Console.WriteLine("  --mode=search              Full-text search over source and symbols.");
        Console.WriteLine("  --mode=find-symbol         Resolve a symbol by FQN.");
        Console.WriteLine("  --mode=diff                Show semantic changes between two snapshots.");
        Console.WriteLine("  --mode=impact              Trace the impact path of a changed symbol.");
        Console.WriteLine("  --mode=context             Assemble a context capsule for a symbol.");
        Console.WriteLine("  --mode=status              Show the current database status.");
        Console.WriteLine("  --mode=timings             Show step-by-step timing data for a snapshot.");
        Console.WriteLine("  --mode=simulate-rename     Simulate renaming a symbol and show affected references.");
        Console.WriteLine("  --mode=simulate-move       Simulate moving a symbol to a new namespace.");
        Console.WriteLine("  --mode=simulate-remove     Simulate removing a symbol and show cascading impact.");
        Console.WriteLine("  --mode=audit               Run static analysis checks on the index.");
        Console.WriteLine("  --mode=annotate            Attach a user-authored annotation to a symbol.");
        Console.WriteLine("  --mode=get-annotations     Retrieve annotations for a symbol.");
        Console.WriteLine();
        Console.WriteLine("INDEXING (--mode=index)");
        Console.WriteLine("  Required:");
        Console.WriteLine("    --solution=<path>     Path to the .sln or .slnx file.");
        Console.WriteLine("    --output-dir=<path>   Directory where index.db is stored.");
        Console.WriteLine();
        Console.WriteLine("  Optional:");
        Console.WriteLine("    --strategy=<full|incremental>");
        Console.WriteLine("        full:        Index every document from scratch.");
        Console.WriteLine("                     This is the DEFINITION OF CORRECTNESS for the index.");
        Console.WriteLine("                     Use it as the recovery mechanism when something looks");
        Console.WriteLine("                     wrong: run '--strategy=full' to reset the index to a");
        Console.WriteLine("                     known-good state.");
        Console.WriteLine("        incremental: Only re-index changed documents; reuses facts for");
        Console.WriteLine("                     unchanged documents from the previous snapshot.");
        Console.WriteLine("                     Default on subsequent runs (after an initial full index).");
        Console.WriteLine("        Default: 'full' on first run (no snapshot exists),");
        Console.WriteLine("                 'incremental' on subsequent runs.");
        Console.WriteLine();
        Console.WriteLine("    --output-json=<path>  Also write the snapshot manifest as JSON.");
        Console.WriteLine("    --skip-adapter=<name> Skip a named framework adapter.");
        Console.WriteLine("                          Valid: ASP.NET Core, Dependency Injection,");
        Console.WriteLine("                                 MediatR, EF Core, Serialization, Test.");
        Console.WriteLine();
        Console.WriteLine("SIMULATION (--mode=simulate-*)");
        Console.WriteLine("  Required:");
        Console.WriteLine("    --symbol=<id>         The symbol ID to simulate.");
        Console.WriteLine("    --output-dir=<path>   Directory where index.db is stored.");
        Console.WriteLine("  Options:");
        Console.WriteLine("    --new-name=<name>     New simple name (simulate-rename only).");
        Console.WriteLine("    --new-namespace=<ns>  Target namespace (simulate-move only).");
        Console.WriteLine("    --snapshot=<id>       Snapshot to use (default: latest).");
        Console.WriteLine();
        Console.WriteLine("AUDIT (--mode=audit)");
        Console.WriteLine("  Required:");
        Console.WriteLine("    --output-dir=<path>   Directory where index.db is stored.");
        Console.WriteLine("  Options:");
        Console.WriteLine("    --checks=<list>       Comma-separated checks: dead-symbol, untested-surface,");
        Console.WriteLine("                          unregistered-impl, high-fan-out (default: all).");
        Console.WriteLine("    --fan-out-threshold=N Call-count threshold for high-fan-out (default: 20).");
        Console.WriteLine("    --snapshot=<id>       Snapshot to use (default: latest).");
        Console.WriteLine();
        Console.WriteLine("ANNOTATIONS (--mode=annotate / --mode=get-annotations)");
        Console.WriteLine("  Required:");
        Console.WriteLine("    --output-dir=<path>   Directory where index.db is stored.");
        Console.WriteLine("  Options:");
        Console.WriteLine("    --symbol=<id>         The symbol ID to annotate or query.");
        Console.WriteLine("    --kind=<kind>         Annotation kind (annotate only, required).");
        Console.WriteLine("    --value=<text>        Annotation value (annotate only, required).");
        Console.WriteLine("    --snapshot=<id>       Snapshot to use (default: latest).");
        Console.WriteLine();
        Console.WriteLine("STATUS (--mode=status)");
        Console.WriteLine("  Required:");
        Console.WriteLine("    --output-dir=<path>   Directory where index.db is stored.");
        Console.WriteLine("  Options:");
        Console.WriteLine("    --solution=<path>     If provided, compares the current workspace against");
        Console.WriteLine("                          the latest snapshot and reports freshness mismatches.");
        Console.WriteLine("                          Without it, only schema version and latest snapshot");
        Console.WriteLine("                          ID are reported.");
        Console.WriteLine("    --json                Emit structured JSON instead of plain text.");
        Console.WriteLine();
        Console.WriteLine("TIMINGS (--mode=timings)");
        Console.WriteLine("  Required:");
        Console.WriteLine("    --output-dir=<path>   Directory where index.db is stored.");
        Console.WriteLine("  Options:");
        Console.WriteLine("    --snapshot=<id>       Show timings for a specific snapshot (default: latest).");
        Console.WriteLine("    --json                Emit structured JSON instead of plain text.");
        Console.WriteLine();
        Console.WriteLine("SNAPSHOT LIFECYCLE");
        Console.WriteLine("  Each indexing run (full or incremental) creates a NEW snapshot.");
        Console.WriteLine("  The last 3 snapshots are retained; older ones are pruned automatically.");
        Console.WriteLine("  Snapshots are never mutated — incremental creates a new snapshot,");
        Console.WriteLine("  it does NOT modify the previous one.");
        Console.WriteLine();
        Console.WriteLine("ENVIRONMENT VARIABLES");
        Console.WriteLine("  INDEXER_SOLUTION_PATH   Equivalent to --solution=.");
        Console.WriteLine("  INDEXER_OUTPUT_DIR      Equivalent to --output-dir=.");
    }
}
