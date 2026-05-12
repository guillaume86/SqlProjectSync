# Changelog

All notable changes to **SqlProjectSync** are documented here. The format is loosely
based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project
follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `SchemaSync` static facade (`Compare`, `Apply`) backed by DacFx's public
  `SchemaCompareProjectEndpoint` + `SchemaComparisonResult.PublishChangesToProject`.
- `SyncOptions`, `SchemaSyncResult`, `PublishResult`, and `SchemaSyncException`
  public types.
- `SqlProjectStyle` enum and `Detect(path)` helper covering both attribute-form
  (`Sdk="Microsoft.Build.Sql/2.1.0"`) and child-element form
  (`<Sdk Name="Microsoft.Build.Sql" />`) SDK projects, plus legacy SSDT projects.
- Internal `ScmpModel` reader that walks parent directories to resolve the target
  `.sqlproj` by `ProjectBasedModelProvider.Name`.
- Internal `DspReader` that reads `<DSP>` → `<SqlServerVersion>` → `Sql160` default.
- `sqlproj-sync` global tool with a single `sync` verb (positional `.scmp` path,
  `--preview`, `-v|--verbosity`, `--folder-structure`).
- SDK-style and legacy `.sqlproj` test fixtures with seven schema objects each.
- xUnit v3 + Shouldly test suite: 24 unit tests, 5 SDK integration tests against
  each of two backends (LocalDB on Windows and a SQL Server 2022 Testcontainers
  fixture on Linux) for a total of 10 backend-isolated scenarios, plus a gated
  3-test legacy-compat suite.
- `IDatabaseFixture` abstraction with `LocalDbFixture` and `SqlContainerFixture`
  implementations; integration tests inherit from `SyncIntegrationTestsBase` and
  bind a backend via `IClassFixture<T>`.
- GitHub Actions: `ci.yml` (build + tests on Windows), `release.yml` (pack + NuGet
  push on tag), `legacy-compat.yml` (nightly best-effort legacy run).

### Notes

- Compare options/exclusions from the `.scmp` are loaded via DacFx's public
  `new SchemaComparison(scmpPath)` constructor; a reflection-based fallback ships
  but has never triggered in our test runs.
- The fixture DSP is `Sql150` (SQL Server 2019) for LocalDB compatibility. Adjust
  if your environment uses a newer LocalDB.
- See [PLAN.md](./PLAN.md) for the phased implementation log and per-phase
  follow-on items.

## [0.1.0] — Unreleased

Initial public release will be tagged `v0.1.0` once Phase 7 lands and the test
matrix has run green in CI. Until then this changelog tracks unreleased work.
