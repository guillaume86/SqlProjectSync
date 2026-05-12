# CLAUDE.md — guidance for the assistant working in this repo

## Project purpose

**SqlProjectSync** pulls a SQL Server database's schema **into** a `.sqlproj`. Modern [`Microsoft.Build.Sql`](https://github.com/microsoft/DacFx/tree/main/src/Microsoft.Build.Sql) SDK-style projects are first-class; legacy SSDT `.sqlproj` is best-effort.

All schema-compare / project-publish work goes through the public DacFx APIs:
[`SchemaCompareProjectEndpoint`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.sqlserver.dac.compare.schemacompareprojectendpoint) +
[`SchemaComparisonResult.PublishChangesToProject(...)`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.sqlserver.dac.compare.schemacomparisonresult.publishchangestoproject) +
[`DacExtractTarget`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.sqlserver.dac.dacextracttarget).

No Visual Studio dependency. No `Microsoft.Build.Locator`. No vswhere. No XDocument-patching of `.sqlproj`. No proxies into VS-internal assemblies.

## Repo layout

```
src/
├── SqlProjectSync/             # core library, packable as `SqlProjectSync`
└── SqlProjectSync.Tool/        # CLI global tool, packable as `SqlProjectSync.Tool`
tests/
├── SqlProjectSync.Tests/                  # xUnit v3 unit tests
├── SqlProjectSync.IntegrationTests/       # xUnit v3 LocalDB-backed tests
└── Fixtures/
    ├── SdkStyleTestProject/               # Microsoft.Build.Sql 2.x SDK fixture (primary)
    └── LegacyTestProject/                 # legacy .sqlproj fixture (best-effort)
.github/workflows/                         # ci.yml, release.yml, legacy-compat.yml
Directory.Build.props                      # shared compiler / nullable / warnings settings
Directory.Packages.props                   # central package versions
```

See [PLAN.md](./PLAN.md) for the phased implementation.

## Conventions

### Compiler / language
- **TargetFramework**: `net10.0` only. LTS, released Nov 2025. No multi-targeting unless a concrete consumer demands it.
- **LangVersion**: `latest` (currently C# 14).
- **Nullable**: enabled everywhere. Treat any nullable warning as an error.
- **ImplicitUsings**: enabled.
- **TreatWarningsAsErrors**: `true`. Don't suppress warnings to make a build green — fix the root cause or scope the suppression with a justified `#pragma` / `[SuppressMessage]` comment.

### Test stack
- **xUnit v3** (`xunit.v3` 3.x — GA). Not xUnit v2. Not NUnit. Not MSTest.
- **Shouldly** for assertions. **Do not introduce FluentAssertions** — version 8.x and above is commercial-licensed.
- LocalDB connections via `Microsoft.Data.SqlClient` (not the legacy `System.Data.SqlClient`).
- Test config via `Microsoft.Extensions.Configuration` + env-var override (`SQLPROJECTSYNC_*`). No bespoke `AppSettings` helper class.

### CLI
- **System.CommandLine** 2.0+ (stable GA since April 2026). Not CommandLineParser. Not Cocona.
- Verbs match the README: `sync` is the only one. No stub verbs.

### Package management
- Central package versions via `Directory.Packages.props` with `ManagePackageVersionsCentrally=true`. Don't add `Version="..."` attributes directly on `<PackageReference>` items in csprojs.
- When bumping a dependency, prefer the latest stable on nuget.org. **Verify the version on the live nuget.org page** before pinning. Record the version + the date in the commit body.

### Commits
- **Conventional commits** are required: `feat:`, `fix:`, `chore:`, `test:`, `docs:`, `refactor:`, `ci:`, `build:`, `perf:`, `style:`. Scope is optional and in parentheses: `feat(core): ...`, `test(integration): ...`.
- One logical change per commit. No "wip" commits on `main`.
- Breaking changes use `!` after the type/scope: `feat(tool)!: rename --verbose to -v`.

### Public API
- Prefer additive changes. Semver gates breaking changes on the public surface of `SqlProjectSync`.
- Keep the surface small. Default to `internal` and only promote to `public` when something has a real call site outside the assembly.

## Do

- Use `Microsoft.SqlServer.Dac.Compare` public APIs (`SchemaComparison`, `SchemaCompareProjectEndpoint`, `SchemaComparisonResult.PublishChangesToProject`).
- Detect SDK vs legacy `.sqlproj` style via the `SqlProjectStyle.Detect(path)` helper. Treat SDK as the default; legacy as best-effort.
- For `.sqlproj` structural edits beyond what `PublishChangesToProject` handles (e.g. adding `SqlCmdVariable`, `PreDeploy` items), evaluate the preview `Microsoft.SqlServer.DacFx.Projects` package (namespace `Microsoft.SqlServer.Dac.Projects`, type `SqlProject`). Pin to a specific preview version and isolate behind an internal helper.
- Build SDK fixtures with `dotnet build` from the integration test fixture lifecycle, not committed `.dacpac` files.
- Use `ILogger` / `ILogger<T>` from `Microsoft.Extensions.Logging.Abstractions` in the core library. Console logging belongs in the CLI project, not the library.

## Don't

- Don't hand-patch `.sqlproj` XML with `XDocument`. The SDK auto-globs `**/*.sql` (`<Build Include="**/*.sql" />` in `Microsoft.Build.Sql/sdk/Sdk.props`). No `<Build Include="..."/>` or `<Folder Include="..."/>` items are needed per file.
- Don't copy `Microsoft.VisualStudio.Data.Tools.Package.*` proxies or any VS-internal types. The legacy repo did this; the new repo gets the same behavior from `DacExtractTarget`.
- Don't take a dependency on Visual Studio's MSBuild, vswhere, or `Microsoft.Build.Locator`. `dotnet build` is enough for SDK projects.
- Don't add `Microsoft.Build` packages unless the operation truly requires programmatic MSBuild evaluation (it doesn't, in the planned scope).
- Don't introduce FluentAssertions. (See the test stack section.)
- Don't reach for new abstractions for hypothetical extensibility. The legacy repo carried half-implemented abstractions (`ModelProvider` hierarchy, commented-out `SqlProjectProperties`). We do not.

## How to add a sync scenario test

1. Add a method to `SyncIntegrationTests` (or a peer class) that takes `LocalDbFixture` via `IClassFixture<LocalDbFixture>`.
2. Mutate the LocalDB schema with raw SQL via `Microsoft.Data.SqlClient` to set up the scenario (drop a table, add a column, etc.).
3. Call `SchemaSync.Compare(scmpPath)` / `SchemaSync.Apply(result)`.
4. Assert on `PublishResult.AddedFiles` / `DeletedFiles` / `ChangedFiles` and the on-disk state of the fixture project directory.
5. After the test, revert the schema mutation in a `try/finally` or via `LocalDbFixture` reset hooks so subsequent tests start clean.

## How to run

```bash
# Unit tests (no DB needed)
dotnet test tests/SqlProjectSync.Tests

# SDK integration tests (requires LocalDB on Windows)
dotnet test tests/SqlProjectSync.IntegrationTests --filter "Style!=Legacy"

# Legacy compat tests (best-effort, off by default)
$env:SQLPROJECTSYNC_RUN_LEGACY = "1"  # PowerShell
dotnet test tests/SqlProjectSync.IntegrationTests --filter "Style=Legacy"
```

LocalDB connection can be overridden with `SQLPROJECTSYNC_CONNECTION`.

## Where things live

| Concern | File |
|---|---|
| Public facade | `src/SqlProjectSync/SchemaSync.cs` |
| `.scmp` parsing | `src/SqlProjectSync/ScmpModel.cs` |
| SDK vs legacy detector | `src/SqlProjectSync/SqlProjectStyle.cs` |
| Options record | `src/SqlProjectSync/SyncOptions.cs` |
| CLI entry | `src/SqlProjectSync.Tool/Program.cs` |
| LocalDB lifecycle | `tests/SqlProjectSync.IntegrationTests/LocalDbFixture.cs` |
| Sync scenarios | `tests/SqlProjectSync.IntegrationTests/SyncIntegrationTests.cs` |
| Legacy compat | `tests/SqlProjectSync.IntegrationTests/LegacyCompatTests.cs` |
| SDK fixture | `tests/Fixtures/SdkStyleTestProject/` |
| Legacy fixture | `tests/Fixtures/LegacyTestProject/` |

## When in doubt

- Consult the legacy repo for the *behavior* we need to preserve, never for code to copy. Tests there name the scenarios we have to re-establish.
- The first-party reference implementation of "apply schema compare to a project" is in [`microsoft/sqltoolsservice` `SchemaComparePublishProjectChangesOperation.cs`](https://github.com/microsoft/sqltoolsservice/blob/main/src/Microsoft.SqlTools.SqlCore/SchemaCompare/SchemaComparePublishProjectChangesOperation.cs). It is one line of real work — keep our facade nearly as thin.
