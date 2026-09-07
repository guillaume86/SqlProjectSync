# Changelog

All notable changes to **SqlProjectSync** are documented here. The format is loosely
based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project
follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.4.4] — 2026-09-07

### Fixed

- `ChangedTableRewriter` no longer stacks a trigger's header comment on every
  sync. SQL Server stores a trigger's leading comments as part of its
  definition, so the script DacFx produces for a changed trigger already
  carries the header; the rewriter spliced it in over the `CREATE TRIGGER`
  fragment only, leaving the file's existing header above it. Because comments
  take part in the comparison, the now-mismatched trigger was reported as
  changed again on the next sync, and the stack grew by one copy per run —
  70 copies on Mpleo's `BsItem.sql`. A database deployed from such a file
  inherits the stack and doubles it on the next sync. Replaced and removed
  standalone statements now take the comment run that precedes them along
  (`LeadingCommentStart`), and generated scripts are trimmed on both ends so
  the newline SQL Server keeps in front of a stored definition no longer lands
  as a stray blank line.

## [0.4.3] — 2026-06-18

### Fixed

- `SchemaSync.Apply` no longer crashes with
  `startIndex ('-1') must be a non-negative value` on table changes. DacFx's
  `PublishChangesToProject` processes a changed table's inline column/constraint
  children first — rewriting the `.sql` file in place — and then fails to locate
  the table's original script in the now-modified file, aborting the whole
  publish with nothing written. Observed on Mpleo for a column rename with a
  referencing foreign key and for a primary-key column change. The new
  `ChangedTableRewriter` takes those table diffs over: it excludes them from the
  DacFx publish and rewrites each file itself, splicing the `CREATE TABLE` by
  offset with the freshly-scripted source definition while preserving untouched
  statements (e.g. an unchanged index or trigger in the same file) and applying
  standalone children (new/dropped indexes) separately.
  `ChangedTableRewriterTests.RawPublishChangesToProject_StillCrashesOnColumnRename`
  is the tripwire that flips when DacFx fixes the bug upstream.

## [0.4.2] — 2026-05-15

### Changed

- `LogDropped` (per-constraint dedup message) moves from Information to
  Debug. Same reasoning as `LogLifted` in 0.4.0: on a sync that triggers
  the workaround across many tables, the per-constraint log fans out and
  drowns the higher-signal events.

## [0.4.1] — 2026-05-15

### Fixed

- `InlineConstraintFolder` no longer leaves stranded blank lines after a
  run of removed `ALTER TABLE ADD CONSTRAINT` batches. Each removed batch
  had a preceding blank-line separator that was not part of its removal
  span — observed on Mpleo's `AkAsk.sql` where 22 lifted ALTERs left 21
  blank lines between `CREATE TABLE` and the trailing `CREATE TRIGGER`.
  `ComputeRemovalSpan` now keeps consuming whitespace-only lines after
  `GO` until the next non-blank line, so adjacent removed batches collapse
  to no residue. The single blank line that was originally between the
  kept `CREATE TABLE` and the first removed `ALTER` survives — matches
  the section-separator convention.

## [0.4.0] — 2026-05-15

### Added

- `SyncOptions.TrimLeadingBlankLines` (opt-in, default `false`) and matching
  `--trim-leading-blanks` CLI flag. When enabled, `SchemaSync.Apply` strips
  leading whitespace-only lines from every touched `.sql` file via the new
  `LeadingBlankTrimmer`. DacFx occasionally emits a blank line before the
  first statement (especially before a leading comment that precedes a
  `CREATE TABLE`); enabling this option keeps the first-sync diff quiet.

### Fixed

- `InlineConstraintFolder` no longer drags CRLF into a previously LF-only
  file (or vice versa) when rewriting a `CREATE TABLE` under
  `ModelFidelity`. `Sql160ScriptGenerator` hard-codes CRLF in its output;
  the folder now sniffs the source file's line ending and normalizes the
  regen to match.
- `ProjectScriptCollector` resolves legacy `<Build Include="dbo\Tables\..."/>`
  paths correctly on non-Windows hosts. The collector previously only
  swapped `/` for `Path.DirectorySeparatorChar`, so on Linux the unchanged
  backslashes became literal filename characters and `File.Exists` dropped
  every legacy build item. Three previously red Linux-CI tests now pass.

### Changed

- `SchemaSync` Information-level logs (running compare, comparison complete,
  updating project, save complete, per-file add/remove/change) move to
  Debug. Console output is the caller's responsibility — callers who want
  a familiar one-line-per-file summary can iterate `PublishResult.AddedFiles`
  / `ChangedFiles` / `DeletedFiles` themselves.
- `LogLifted` (per-constraint lift message under `ModelFidelity`) moves to
  Trace. It can fan out to hundreds of lines on a first sync after a tool
  swap and is diagnostic noise at Information.

## [0.3.0] — 2026-05-15

### Added

- Workaround for [DacFx #792](https://github.com/microsoft/DacFx/issues/792):
  `SchemaSync.Apply` now post-processes `PublishChangesToProject`'s output to
  fold trailing `ALTER TABLE ADD CONSTRAINT` statements relative to the
  matching `CREATE TABLE` in the same file.
  - **Dedup** (always on): drops every standalone `ALTER` whose constraint
    name is already declared inline. Required — the duplicate fails model
    validation on subsequent compares and breaks the round-trip.
  - **Lift** (opt-in via `SyncOptions.InlineConstraintsMode = ModelFidelity`):
    also rewrites standalone-only constraints as inline constraints inside
    `CREATE TABLE`. Useful when migrating an existing project from tooling
    that emitted inline-form constraints, so first-sync diffs stay small.
- Public enum `InlineConstraintsMode` (`None`, `ModelFidelity`), name and
  values mirroring DacFx's internal `CreateTableInlineConstraintsMode`.
- `--inline-constraints None|ModelFidelity` option on the `sync` CLI verb.
- Integration tripwire `DacFxIssue792Tests` that bypasses SqlProjectSync,
  drives DacFx directly with a minimal repro, and asserts the upstream bug
  is still present. When the assertion starts failing, the workaround should
  be removed.

### Changed

- Bumped `Microsoft.SqlServer.DacFx` to `170.4.80-preview` and
  `Microsoft.SqlServer.DacFx.Projects` to `0.6.3-preview`. Both still ship
  the bug; the bump just keeps us tracking the latest preview while
  waiting for an upstream fix.

## [0.2.1] — 2026-05-13

### Fixed

- `SchemaSync.Compare` no longer hands every `.sql` file under the project
  directory to `SchemaCompareProjectEndpoint`. On real-world projects with
  build outputs under `bin/` / `obj/`, a root `Script.PostDeployment.sql`, or
  data files under `Version/Data/`, the previous walk made `PublishChangesToProject`
  fail with `ArgumentNullException: Value cannot be null. (Parameter 'source')`
  after several minutes of parsing duplicate model objects.
- New internal `ProjectScriptCollector`:
  - Legacy `.sqlproj`: parses `<Build Include="…"/>` items only; missing-on-disk
    items emit a warning and are dropped.
  - SDK `.sqlproj`: globs `**/*.sql` minus `bin/`, `obj/`, `<Build Remove>`,
    and non-Build items (`<None>`, `<Content>`, `<PreDeploy>`, `<PostDeploy>`,
    `<NotInBuild>`, `<RefactorLog>`).

### Added

- Step-by-step logging that mirrors the legacy library's output: running
  comparison → resolved target (DSP + script count) → comparison complete with
  diff count → per-difference at Debug → updating project → per-file
  Add/Remove/Change → save complete. All emitted via `[LoggerMessage]` source
  generators.

## [0.2.0] — 2026-05-12

### Added

- `ScmpModel.Load` now accepts modern DacFx 170.x `.scmp` shape
  (`<ProjectFilePath>` + `<TargetScripts>` + `<Dsp>` inside
  `<ProjectBasedModelProvider>`) in addition to the legacy VS-SSDT shape
  (`<ProjectGuid>` + `<Name>`). Shape is detected once via XDocument probe
  and carried as an internal `ScmpShape` enum.
- Internal `LegacyScmpReader` parses legacy `<ConfigurationOptionsElement>`
  options and `<ExcludedSourceElements>` / `<ExcludedTargetElements>`
  exclusions into `DacDeployOptions` and `SchemaComparisonExcludedObjectId`
  by reflection + type coercion (bool/int/long/string/enum/nullable).

### Fixed

- Legacy VS-SSDT `.scmp` files now round-trip their options and exclusions.
  DacFx 170.x rejects the legacy shape, so the previous code fell back to
  defaults and silently dropped every `<ConfigurationOptionsElement>` entry
  and every type/object exclusion. `LegacyScmpReader` now reads them
  directly off the XDocument.
- `<Value>ExcludedType</Value>` entries (legacy type-level exclusions for
  e.g. `SqlUser`, `SqlRole`) are routed to `DacDeployOptions.ExcludeObjectTypes`
  via the same CLR-Type → `ObjectType` mapping DacFx uses internally.
- Older VS-SSDT per-type flags (`DoNotDropXxx=True`, `ExcludeXxx=True`) and
  the newer `DoNotDropTypes` semicolon-list shape both contribute to
  `DoNotDropObjectTypes` / `ExcludeObjectTypes`.
- Case-insensitive property lookup so legacy casing drift
  (`NoAlterStatementsToChangeCLRTypes` vs the current `…ClrTypes`) resolves.
- Empty `<Name/>` placeholders inside `<SelectedItem>` are skipped when
  building an `ObjectIdentifier`, matching DacFx's own
  `SchemaCompareElementId.ReadTypeAndNamePartsFromXmlDoc` behaviour.
- `SchemaComparison.LoadFromXml` reflection fallback removed — it never
  resolved on DacFx 170.x.

### Notes

- Unknown option names that don't resolve to a `DacDeployOptions` property
  and aren't recognised as a type-flag pattern are logged at Warning and
  skipped (UI-only or renamed). A small allowlist of well-known non-options
  (`PlanGenerationType`, `TargetConnectionString`, `TargetDatabaseName`,
  `AllowExistingModelErrors`) is silently ignored.
- Uncoercible values on known `DacDeployOptions` properties still throw
  `SchemaSyncException`.

## [0.1.0] — 2026-05-12

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
  a SQL Server 2022 Testcontainers fixture, plus a 3-test legacy-compat suite.
- `IDatabaseFixture` abstraction with a `SqlContainerFixture` implementation;
  integration tests inherit from `SyncIntegrationTestsBase` and bind the fixture
  via `IClassFixture<SqlContainerFixture>`.
- GitHub Actions: `ci.yml` (build + tests on Linux against the SQL Server 2022
  container), `release.yml` (pack + NuGet push on tag), `legacy-compat.yml`
  (nightly best-effort legacy run, also on Linux).

### Notes

- Compare options/exclusions from the `.scmp` are loaded via DacFx's public
  `new SchemaComparison(scmpPath)` constructor; a reflection-based fallback ships
  but has never triggered in our test runs.
- The fixture DSP is `Sql150` (SQL Server 2019). The Sql150 dacpac deploys
  cleanly onto the SQL Server 2022 container the integration tests use (forward
  compat).
- Legacy `.sqlproj` `<Build Include="..."/>` items are kept in sync with on-disk
  files via the `Microsoft.SqlServer.DacFx.Projects 0.5.22-preview` package
  ([LegacyProjectPatcher](src/SqlProjectSync/LegacyProjectPatcher.cs)) — DacFx's
  own `PublishChangesToProject` only writes/deletes the `.sql` files. The 0.5.x
  line depends on stable `Microsoft.SqlServer.DacFx 170.2.70`, so the core
  library stays on stable DacFx `170.3.93`. The newer 0.6.x line forces a
  preview DacFx; revisit if/when 0.6.x's additional API is needed.
- See [PLAN.md](./PLAN.md) for the phased implementation log and per-phase
  follow-on items.

### Versioning

Package versions are derived from git tags by [MinVer](https://github.com/adamralph/minver).
Tags use the `v` prefix (e.g. `v0.1.0`); untagged builds emit
`0.0.0-alpha.0.<commit-height>+<sha>`. The CI workflows fetch full git history
(`fetch-depth: 0`) so MinVer can see the tags.

