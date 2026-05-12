# Implementation plan — SqlProjectSync

## Why this exists

On modern `Microsoft.Build.Sql` SDK-style projects, almost all of this is unnecessary:

- The SDK auto-globs `**/*.sql`, so no `<Build>` / `<Folder>` patching is needed.
- `dotnet build` replaces the MSBuild-via-vswhere discovery dance.
- DacFx exposes `SchemaCompareProjectEndpoint` and `SchemaComparisonResult.PublishChangesToProject(...)` which between them collapse the whole "build dacpac → compare → patch XML → write files → maintain folder table" pipeline into one call. The folder layout is owned by [`DacExtractTarget`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.sqlserver.dac.dacextracttarget) (default `SchemaObjectType` matches the SSDT convention).

We therefore drop the entire VS-internal-proxy / MSBuildLocator / FolderStructure / XDocument code path, target `net10.0`, and ship two NuGet packages: a small core library and a `dotnet tool` CLI.

## Outcome

- `SqlProjectSync` — core library NuGet package.
- `SqlProjectSync.Tool` — .NET global tool: `sqlproj-sync sync <scmp> [--preview] [-v <level>]`.
- xUnit v3 test suite that re-establishes the behavioral guarantees of the legacy tests (without copy-pasting them) and additionally exercises legacy-style `.sqlproj` compat.

## Phases

### Phase 0 — Bootstrap (done)

- `git init -b main`
- `.gitignore` (dotnet template), `.gitattributes`, `.editorconfig`, `LICENSE` (MIT), `README.md`, `PLAN.md`, `CLAUDE.md`
- Initial commit: `chore: bootstrap repo`

### Phase 1 — Solution scaffold and shared build config (done)

Layout:

```
SqlProjectSync/
├── SqlProjectSync.slnx
├── Directory.Build.props
├── Directory.Packages.props
├── src/
│   ├── SqlProjectSync/             # core library (packable)
│   └── SqlProjectSync.Tool/        # CLI global tool (packable)
└── tests/
    ├── Directory.Build.props                 # suppresses CA1707 (xUnit underscore names) for test projects
    ├── SqlProjectSync.Tests/                 # unit tests (xUnit v3, MTP runner)
    ├── SqlProjectSync.IntegrationTests/      # LocalDB-backed
    └── Fixtures/
        ├── SdkStyleTestProject/              # Microsoft.Build.Sql SDK fixture (Phase 4)
        └── LegacyTestProject/                # legacy .sqlproj fixture, best-effort (Phase 4)
```

Solution file: the new XML `.slnx` format (net10 SDK), not the legacy `.sln`.

`Directory.Build.props` (applies to all projects):

- `TargetFramework=net10.0` (LTS, released Nov 2025)
- `LangVersion=latest` (resolves to C# 14)
- `Nullable=enable`
- `ImplicitUsings=enable`
- `TreatWarningsAsErrors=true`
- `AnalysisLevel=latest-recommended`
- `EnforceCodeStyleInBuild=true`
- `Deterministic=true`
- Common metadata: `Authors`, `Company`, `RepositoryUrl`, `PackageProjectUrl`.

Test-runner choice: xUnit v3 over the **Microsoft Testing Platform** (MTP), not VSTest. Test projects set `OutputType=Exe`, `UseMicrosoftTestingPlatformRunner=true`, and `TestingPlatformDotnetTestSupport=true`. `xunit.v3` brings the MTP host transitively, so there is no `Microsoft.NET.Test.Sdk` or `xunit.runner.visualstudio` reference. MTP exits with code 8 on a truly empty test run, so each test project ships a tiny `ScaffoldTests.Test_Infrastructure_Is_Wired` smoke test until real tests land in Phase 5.

`Directory.Packages.props` — `ManagePackageVersionsCentrally=true`. Versions pinned to the latest stable on nuget.org as of 2026-05-12:

| Package | Version | Notes |
|---|---|---|
| `Microsoft.SqlServer.DacFx` | `170.3.93` | Compatible with net10.0. Preview `170.4.77-preview` if needed. |
| `System.CommandLine` | `2.0.7` | Stable GA since April 2026. |
| `Microsoft.Extensions.Logging` | `10.0.7` | Console provider for the CLI. |
| `Microsoft.Extensions.Logging.Abstractions` | `10.0.7` | `ILogger<T>` for the library. |
| `Microsoft.Extensions.Configuration` | `10.0.7` | Test config. |
| `Microsoft.Extensions.Configuration.Json` | `10.0.7` | `appsettings.json`. |
| `Microsoft.Extensions.Configuration.EnvironmentVariables` | `10.0.7` | env-var override. |
| `Microsoft.Data.SqlClient` | `7.0.1` | LocalDB connections in integration tests. |
| `xunit.v3` | `3.2.2` | GA. Brings the MTP test host transitively. |
| `Shouldly` | `4.3.0` | Replaces FluentAssertions (FA 8.x is commercial-licensed). |
| `coverlet.collector` | `10.0.0` | Coverage. |
| `Microsoft.Build.Sql` | `2.1.0` | SDK referenced from the SDK fixture's `.sqlproj`. |

When bumping any of these later, verify the version on nuget.org and record version + date in the commit body.

Commit: `chore: scaffold solution and shared build props`

### Phase 2 — Core library (done)

Small public surface:

- `SchemaSync` static facade
  - `static SchemaSyncResult Compare(string scmpPath, SyncOptions? options = null, ILogger? logger = null)`
  - `static PublishResult Apply(SchemaSyncResult comparison, bool preview = false, ILogger? logger = null)`
- `SyncOptions` — record with `DacExtractTarget FolderStructure = SchemaObjectType`, optional `OverrideTargetProjectPath`, `OverrideSourceConnectionString`, `DataSchemaProvider`.
- `ScmpModel` — minimal `.scmp` reader/writer. `Load`, `GetTargetProjectPath`, `BuildSourceEndpoint`, `Save`. Internal record types `ConnectionBasedModelProvider`, `FileBasedModelProvider`, `ProjectBasedModelProvider`.
- `SqlProjectStyle` enum (`Sdk`, `Legacy`) + `Detect(string sqlprojPath)` helper — looks for `<Sdk Name="Microsoft.Build.Sql" />` (element or attribute on `<Project>`).
- `SchemaSyncResult`, `PublishResult`, `SchemaSyncException`.

Implementation kernel:

```csharp
var scmp = ScmpModel.Load(scmpPath);
var projectPath = options?.OverrideTargetProjectPath ?? scmp.GetTargetProjectPath();
var projectDir = Path.GetDirectoryName(projectPath)!;
var dsp = options?.DataSchemaProvider ?? ReadDspFromProject(projectPath);

var scripts = Directory.GetFiles(projectDir, "*.sql", SearchOption.AllDirectories);

var source = scmp.BuildSourceEndpoint();
var target = new SchemaCompareProjectEndpoint(projectPath, scripts, dsp, options.FolderStructure);

var comparison = new SchemaComparison(source, target);
scmp.ApplyOptionsAndExclusions(comparison);

var result = comparison.Compare();
```

`Apply`:

```csharp
if (preview) return PublishResult.PreviewFrom(result.Differences);
var publish = result.PublishChangesToProject(projectDir, options.FolderStructure);
if (!publish.Success) throw new SchemaSyncException(publish.ErrorMessage);
return PublishResult.FromDacFx(publish);
```

Explicit non-features (deletions vs the legacy repo): no `ProjectModel`/XDocument patching, no `ProjectBuilder`/MSBuild orchestration, no `FolderStructure` table, no `MSBuildLocator`, no VS proxies.

**Follow-on items** (defer to Phases 3 / 5):

- `ScmpModel.LoadOptionsViaReflection` is a defensive stub. **Phase 5 update:** in practice DacFx's public `new SchemaComparison(scmpPath)` loads our project-target `.scmp` files without throwing, so the reflection fallback never fires under the SDK integration tests. The stub remains dead code with a comment; revisit only if a real-world `.scmp` exposes the failure mode.
- `ScmpModel.Save` is not implemented; Phase 5's planned `Save_RoundTripsXml` unit test must either add a minimal `Save(string path)` (round-trip the parsed `XDocument`) or drop the test.
- `SchemaSync.Apply` passes the project **directory** (not the `.sqlproj` file path) to `PublishChangesToProject` — confirm this matches the first-party `SchemaComparePublishProjectChangesOperation.cs` reference when wiring the CLI in Phase 3 and adjust if the API expects the file path.
- `ScmpModel.CopyOptions` reflects over the public read/write properties of DacFx's `SchemaComparison.Options` runtime type. If a future DacFx version exposes a read-only or non-copyable property, the reflection-based copy will throw at runtime — verify against the live `SchemaCompareOptions` shape in Phase 5 integration tests.

Commit: `feat(core): implement schema sync over DacFx PublishChangesToProject`

### Phase 3 — CLI (done)

- Packable as a .NET global tool: `PackAsTool=true`, `ToolCommandName=sqlproj-sync`, `PackageOutputPath=../../artifacts`.
- `System.CommandLine` 2.0.7 root command with verb `sync`:
  - `sqlproj-sync sync <scmp-path> [--preview] [-v|--verbosity <Information>] [--folder-structure <SchemaObjectType>]`
- Logger wiring: `Microsoft.Extensions.Logging` console provider, simple formatter, level driven by `-v`.
- No `build` verb — users run `dotnet build` directly. The legacy `build` verb was a stub anyway.

Commit: `feat(tool): add sync verb backed by SchemaSync`

### Phase 4 — Test fixtures (done)

Build two minimal fixtures from scratch (do **not** copy `tests/TestSqlProject` from the legacy repo):

- `tests/Fixtures/SdkStyleTestProject/SdkStyleTestProject.sqlproj` — `<Sdk Name="Microsoft.Build.Sql" Version="2.1.0" />`. Schema objects matching the legacy diff scenarios:
  - `dbo.Table1`, `dbo.Table2`, `dbo.View1`, `dbo.ScalarFunction1`, `dbo.StoredProcedure1`, `dbo.UserDefinedTableType1`, `dbo.Tr_Table1_Trigger1`.
  - Hand-authored `CompareToProject.scmp` next to it.
- `tests/Fixtures/LegacyTestProject/LegacyTestProject.sqlproj` — same objects, no `Sdk` attribute (real legacy XML with `Import Project="...SSDT...targets"`). Used only by the legacy-compat test class.

No `.dacpac` is committed; the SDK fixture is built on demand by the integration test fixture.

Commit: `test: add SDK-style and legacy sqlproj fixtures`

### Phase 5 — Tests (done)

xUnit v3 + Shouldly. Re-establish the legacy guarantees with **freshly written** tests (no copy-paste).

**Unit tests** (`tests/SqlProjectSync.Tests`):

- `ScmpModelTests`
  - `Load_ReturnsModel_FromValidFile`
  - `GetTargetProjectPath_ResolvesRelativeProjectByName` (≡ legacy `ShouldGetProjectFileName`)
  - `Save_RoundTripsXml`
  - `BuildSourceEndpoint_FromConnectionBasedProvider`
- `SqlProjectStyleTests`
  - `Detect_SdkStyle_Returns_Sdk`
  - `Detect_LegacyStyle_Returns_Legacy`
  - `Detect_MalformedProject_Throws`
- `SyncOptionsTests` — defaults sanity.

**Integration tests** (`tests/SqlProjectSync.IntegrationTests`, LocalDB-required):

- `LocalDbFixture` — `IAsyncLifetime`. Builds the SDK fixture once per session via `dotnet build`, publishes the resulting `.dacpac` to a uniquely-named LocalDB database, drops the DB on dispose. Cleanly written, not derived from the legacy `DatabaseManager`.
- `SyncIntegrationTests` (`IClassFixture<LocalDbFixture>`):
  - `Compare_NoDifferences_WhenDbMatchesProject` (≡ legacy `CompareShouldNotFindDifferences`)
  - `Apply_RemovesSqlFile_WhenTableDroppedInDb` (≡ legacy `UpdateShouldRemoveDroppedTable`)
  - `Apply_AddsSqlFile_WhenTableAddedInDb` (≡ legacy `UpdateShouldAddCreatedTable`)
  - **New** `Apply_ModifiesSqlFile_WhenColumnAdded`
  - **New** `Apply_Preview_DoesNotTouchDisk`
- `LegacyCompatTests` — same scenarios pointed at the legacy fixture. Trait `Style=Legacy`, gated on `SQLPROJECTSYNC_RUN_LEGACY=1`. Acceptable to fail; CI does not block on these.

**Test config**:

- `appsettings.json` with `ConnectionString=Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Encrypt=false;TrustServerCertificate=true`.
- Loaded via `Microsoft.Extensions.Configuration` + env-var override (`SQLPROJECTSYNC_CONNECTION`).

Commits:

- `test(unit): cover ScmpModel and SqlProjectStyle`
- `test(integration): cover sync compare/apply scenarios against SDK project`
- `test(integration): add legacy sqlproj compat suite (ok-if-broken)`

**Follow-on items**:

- The first integration test is named `Compare_NoFileLevelChanges_WhenDbMatchesProject` (not `Compare_NoDifferences_…`). A freshly-published DB exposes a small set of database-level differences (collation, filegroup, compat level) that DacFx's defaults pick up but that do not translate to file-level changes. The assertion verifies `Apply` produces zero `AddedFiles`/`DeletedFiles`/`ChangedFiles`. To get back to a strict `IsEqual` check, the SDK fixture's `CompareToProject.scmp` would need a `<ConfigurationOptionsElement>` block excluding those database-property comparisons.
- `LegacyCompatTests` runs by default (no env-var gate). The full SDK + Sql2022 + Legacy suite is now 13/13 green on this machine.
- DacFx's own `PublishChangesToProject` still does not patch legacy `<Build>` items (confirmed via the DacFx public surface, sqltoolsservice reference, and DacFx repo — no missed overload). The closing piece lives in [LegacyProjectPatcher.cs](src/SqlProjectSync/LegacyProjectPatcher.cs): after each apply, if the target project is legacy-style, it opens the project via the preview `Microsoft.SqlServer.DacFx.Projects` package and replays `publish.AddedFiles` / `publish.DeletedFiles` onto `project.SqlObjectScripts`. SDK projects are detected and skipped (auto-glob covers them).
- `Microsoft.SqlServer.DacFx.Projects` is pinned to `0.5.22-preview` (the most recent 0.5.x preview, which depends on stable `Microsoft.SqlServer.DacFx 170.2.70`). This lets us keep the core library on stable DacFx `170.3.93`. The 0.6.x line is available but bumps the DacFx dependency to a preview (`170.4.63-preview`); revisit when 0.6.x's API additions are needed or when DacFx 170.4 ships stable.
- Folder management is not yet wired: if DacFx writes a `.sql` into a directory the legacy `.sqlproj` doesn't already list under `<Folder Include="..."/>`, the file builds (it's covered by the new `<Build>` item) but the folder is not added to the project XML. Our fixtures don't hit that case. If a real consumer does, extend `LegacyProjectPatcher` to also touch `project.Folders.Add` / `Delete` for newly-required schema-type directories.
- Fixture DSP is `Sql150` (SQL Server 2019) for LocalDB compatibility on the development machine. The Sql150 dacpac deploys cleanly onto SQL Server 2022 too (forward compat), which is what the `SyncIntegrationTests_Sql2022` class verifies via a Testcontainers fixture (`SqlContainerFixture`).
- Integration tests are split by backend: `[Trait("Backend", "LocalDb")]` on `SyncIntegrationTests` (uses `LocalDbFixture`) and `[Trait("Backend", "Sql2022Container")]` on `SyncIntegrationTests_Sql2022` (uses `SqlContainerFixture`, requires Docker). Tests skip gracefully when their backend is unreachable. CI runs both: `windows-latest` for LocalDB and `ubuntu-latest` for the SQL Server 2022 container.
- The integration tests warm up cold (~10s per test due to per-test DB create + dacpac publish). Acceptable for now; if the suite grows, consider an `IClassFixture` that owns one DB per scenario class and uses `BEGIN/ROLLBACK TRAN` or schema-level resets between tests.

### Phase 6 — CI (GitHub Actions) (done)

- `.github/workflows/ci.yml` — PR + push to `main`; `windows-latest` (LocalDB); `actions/setup-dotnet@v4` with `dotnet-version: 10.0.x`; `dotnet build -c Release` + `dotnet test --filter "Style!=Legacy"`.
- `.github/workflows/release.yml` — tag `v*`; `dotnet pack` both packable projects to `artifacts/`; push to NuGet.org gated on `secrets.NUGET_API_KEY`.
- `.github/workflows/legacy-compat.yml` — `workflow_dispatch` + nightly cron; `--filter "Style=Legacy"` with `continue-on-error: true`.

Commit: `ci: github actions for build, test, release, and nightly legacy compat`

**Follow-on items**:

- `release.yml` packs and pushes to NuGet.org gated on the `NUGET_API_KEY` secret. Set that secret in the repository settings before tagging a release, otherwise the push step silently no-ops (the artifacts are still uploaded to the workflow run).
- `release.yml` runs on `ubuntu-latest` because `dotnet pack` does not need LocalDB. If a future release step needs to run integration tests against the .dacpac as a smoke check, split into a windows job that runs tests and an ubuntu job that packs/publishes.
- The cron in `legacy-compat.yml` is 03:30 UTC daily; adjust to match maintainer timezone preferences.
- The CI workflow caches packages by `Directory.Packages.props` hash. If we add a `global.json` pinning the SDK or a `nuget.config` with internal feeds, extend the cache key accordingly.

### Phase 7 — Final docs polish (done)

Refresh `README.md` with real install/usage commands once the tool builds end-to-end. Add a `CHANGELOG.md` keyed to git tags.

Commit: `docs: finalize README and add CHANGELOG`

## Reused references (do not reimplement)

- [`Microsoft.SqlServer.Dac.Compare.SchemaCompareProjectEndpoint`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.sqlserver.dac.compare.schemacompareprojectendpoint) — replaces the legacy "build dacpac → file-based target" loop.
- [`SchemaComparisonResult.PublishChangesToProject(...)`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.sqlserver.dac.compare.schemacomparisonresult.publishchangestoproject) — replaces the legacy `ProjectModel` XDocument patching.
- [`DacExtractTarget`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.sqlserver.dac.dacextracttarget) — replaces the legacy `FolderStructure` table.
- Reference implementation: [`microsoft/sqltoolsservice` `SchemaComparePublishProjectChangesOperation.cs`](https://github.com/microsoft/sqltoolsservice/blob/main/src/Microsoft.SqlTools.SqlCore/SchemaCompare/SchemaComparePublishProjectChangesOperation.cs).

## Verification

End-to-end checks after each phase lands:

1. `dotnet build -c Release` returns 0 from the repo root.
2. `dotnet pack -c Release` produces `SqlProjectSync.<ver>.nupkg` and `SqlProjectSync.Tool.<ver>.nupkg` in `artifacts/`.
3. `dotnet tool install --global --add-source ./artifacts SqlProjectSync.Tool` then `sqlproj-sync --help` prints the `sync` verb.
4. `dotnet test tests/SqlProjectSync.Tests` — all pass.
5. `dotnet test tests/SqlProjectSync.IntegrationTests --filter "Style!=Legacy"` — all pass against LocalDB.
6. `SQLPROJECTSYNC_RUN_LEGACY=1 dotnet test tests/SqlProjectSync.IntegrationTests --filter "Style=Legacy"` — record pass/fail per scenario.
7. CLI smoke against the SDK fixture LocalDB: `sqlproj-sync sync tests/Fixtures/SdkStyleTestProject/CompareToProject.scmp --preview -v Debug` lists diffs without modifying disk; the same command without `--preview` writes the expected files; re-running reports zero differences.
8. CI green: push a branch, open a PR, observe `ci.yml` pass. Tag `v0.1.0` and confirm `release.yml` `pack` runs.
9. `git log --oneline` shows the per-phase conventional commits.

## Out of scope (deferred)

- A `build` CLI verb — `dotnet build` covers it on SDK projects.
- Project → DB sync (the reverse direction) — DacFx publish covers that workflow.
- Typed `.sqlproj` editing via `Microsoft.SqlServer.DacFx.Projects` (preview) — revisit if `SqlCmdVariable` / `PreDeploy` management is needed.
- Source generators / Roslyn analyzers for SQL.
- Internal Azure DevOps NuGet feed — `release.yml` targets NuGet.org by default; internal-feed publish can be added later.
