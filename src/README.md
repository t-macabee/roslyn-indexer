# roslyn-indexer

Generic Roslyn-based semantic indexer for .NET solutions. Produces structured `.codeaudit/` output for AI tooling.

## Required Arguments

```
--solution=PATH      Path to the .sln file (or INDEXER_SOLUTION_PATH env var)
--output-dir=PATH    Output directory for .codeaudit artifacts (or INDEXER_OUTPUT_DIR env var)
```

## Commands

### `--mode=discover`
Enumerate all types across the solution.

```
--mode=discover [--kind=X] [--project=X] [--json]
```

- `--kind=X` — filter by kind category (e.g. `controller`, `service`, `handler`). Comma-separated for multiple.
- `--project=X` — filter to a specific project.
- `--json` — structured JSON output.

### `--mode=structure`
Show dependency graph and fan-in/fan-out for a symbol.

```
--mode=structure --symbol=Namespace.ClassName [--depth=2] [--json]
```

- `--symbol=X` — fully qualified type name (required).
- `--depth=2` — include depth-2 fan-out/fan-in counts.
- `--json` — structured JSON output.

### `--mode=fingerprint`
Compute surface + dependency hash for a symbol.

```
--mode=fingerprint --symbol=Namespace.ClassName
```

- `--symbol=X` — fully qualified type name (required).

### `--mode=who-references`
Find all references to a symbol across the solution.

```
--mode=who-references --symbol=Namespace.ClassName [--json]
```

- `--symbol=X` — fully qualified type name (required).
- `--json` — structured JSON output.

### `--mode=recompute-all`
Recompute fingerprints for all curated semantic entries.

```
--mode=recompute-all
```

### `--mode=mark-dirty`
Mark files as dirty or deleted for incremental re-analysis.

```
--mode=mark-dirty --files=PATH [--deleted=PATH]
```

- `--files=PATH` — path to a text file with one dirty file path per line.
- `--deleted=PATH` — path to a text file with one deleted file path per line.

### `--mode=sweep`
Sweep dirty/deleted files, flag affected semantic entries as stale.

```
--mode=sweep
```

### `--mode=status`
Show current tool state (git root, output dirs, manifest info).

```
--mode=status [--json]
```

### `--mode=lint`
Validate curated semantic entries (checks relationship PascalCase).

```
--mode=lint [--json]
```

### `--mode=impact`
List curated entries affected by the current dirty manifest.

```
--mode=impact [--json]
```

## Global Flags

```
--json     Emit structured JSON output (applies to most modes)
```

## Output Structure

All artifacts go under `<output-dir>/.codeaudit/`:

```
.codeaudit/
  dirty-files.json         — tracks files needing re-analysis
  semantic/
    <sanitized-id>.semantic.json  — curated semantic entries
```

## Environment Variables

| Variable | Purpose |
|---|---|
| `INDEXER_SOLUTION_PATH` | Path to the .sln file (fallback if `--solution` not given) |
| `INDEXER_OUTPUT_DIR` | Output directory (fallback if `--output-dir` not given) |

## License

MIT
