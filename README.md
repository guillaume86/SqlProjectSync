# SqlProjectSync

Pull a SQL Server database's schema **into** a [SQL database project](https://learn.microsoft.com/en-us/sql/tools/sql-database-projects/sql-database-projects) (`.sqlproj`). Modern [`Microsoft.Build.Sql`](https://github.com/microsoft/DacFx/tree/main/src/Microsoft.Build.Sql) SDK-style projects are the first-class target; legacy SSDT `.sqlproj` files are supported on a best-effort basis.

 All schema-compare and project-publish operations go through the public DacFx APIs ([`SchemaCompareProjectEndpoint`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.sqlserver.dac.compare.schemacompareprojectendpoint) and [`SchemaComparisonResult.PublishChangesToProject`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.sqlserver.dac.compare.schemacomparisonresult.publishchangestoproject)), so the tool needs no Visual Studio, vswhere, MSBuild discovery, or hand-rolled folder-structure tables.

## Install

```bash
dotnet tool install --global SqlProjectSync.Tool
```

## CLI

```bash
sqlproj-sync sync <scmp-path> [--preview] [-v <verbosity>] [--folder-structure <layout>]
```

Examples:

```bash
# Preview what would change
sqlproj-sync sync ./CompareToProject.scmp --preview

# Apply changes with debug logging
sqlproj-sync sync ./CompareToProject.scmp -v Debug

# Pick a different DacFx folder layout (default: SchemaObjectType)
sqlproj-sync sync ./CompareToProject.scmp --folder-structure Schema
```

Options:

| Flag | Default | Meaning |
|---|---|---|
| `--preview` | off | Print the projected changes without writing to disk. |
| `-v`, `--verbosity` | `Information` | One of `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`. |
| `--folder-structure` | `SchemaObjectType` | DacFx [`DacExtractTarget`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.sqlserver.dac.dacextracttarget) — controls how files are laid out in the project. |

The `.scmp` file is the same shape as the one Azure Data Studio / SqlPackage produce; the tool resolves the target `.sqlproj` by walking up directories from the `.scmp` and matching `ProjectBasedModelProvider.Name`.

Exit codes: `0` success (or no differences), `1` schema sync failed, `2` comparison returned an invalid state.

## Library

```csharp
using SqlProjectSync;

var comparison = SchemaSync.Compare("CompareToProject.scmp");
if (comparison.IsEqual)
{
    Console.WriteLine("Already in sync.");
    return;
}

var publish = SchemaSync.Apply(comparison);
foreach (var added in publish.AddedFiles)   Console.WriteLine($"+ {added}");
foreach (var del   in publish.DeletedFiles) Console.WriteLine($"- {del}");
foreach (var chg   in publish.ChangedFiles) Console.WriteLine($"~ {chg}");
```

Pass a [`SyncOptions`](src/SqlProjectSync/SyncOptions.cs) record to override the target project path, the source connection string, the DSP, or the folder structure. Pass an `ILogger` to capture structured progress events.

## Status

Phases 0 – 6 are landed. See [PLAN.md](./PLAN.md) for what each phase covered and the per-phase follow-on items.

The legacy `.sqlproj` compat suite (in `tests/SqlProjectSync.IntegrationTests/LegacyCompatTests.cs`) runs nightly in CI under [legacy-compat.yml](.github/workflows/legacy-compat.yml) with `continue-on-error: true`; two of its three tests currently fail because DacFx's `PublishChangesToProject` does not mutate the legacy `.sqlproj` XML. See [PLAN.md](./PLAN.md) Phase 5 follow-on notes.

## Building from source

```bash
dotnet build -c Release SqlProjectSync.slnx
dotnet test  -c Release tests/SqlProjectSync.Tests/SqlProjectSync.Tests.csproj
dotnet test  -c Release tests/SqlProjectSync.IntegrationTests/SqlProjectSync.IntegrationTests.csproj --filter "Style!=Legacy"
dotnet pack  -c Release src/SqlProjectSync/SqlProjectSync.csproj -o artifacts
dotnet pack  -c Release src/SqlProjectSync.Tool/SqlProjectSync.Tool.csproj -o artifacts
```

Integration tests require LocalDB (`(localdb)\MSSQLLocalDB`). Override with `SQLPROJECTSYNC_CONNECTION`. Unit tests have no external dependencies.

## License

MIT — see [LICENSE](./LICENSE).
