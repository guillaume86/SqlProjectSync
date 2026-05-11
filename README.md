# SqlProjectSync

Pull a SQL Server database's schema **into** a [SQL database project](https://learn.microsoft.com/en-us/sql/tools/sql-database-projects/sql-database-projects) (`.sqlproj`). Modern [`Microsoft.Build.Sql`](https://github.com/microsoft/DacFx/tree/main/src/Microsoft.Build.Sql) SDK-style projects are the first-class target; legacy SSDT `.sqlproj` files are supported on a best-effort basis.

 All schema-compare and project-publish operations now go through the public DacFx APIs (`SchemaCompareProjectEndpoint` + `SchemaComparisonResult.PublishChangesToProject`), so the tool no longer needs Visual Studio, vswhere, MSBuild discovery, or hand-rolled folder-structure tables.

## Status

Work in progress. See [PLAN.md](./PLAN.md) for the implementation phases.

## Installation (planned)

```bash
dotnet tool install --global SqlProjectSync
```

## Usage (planned)

```bash
sqlproj-sync sync ./CompareToProject.scmp
sqlproj-sync sync ./CompareToProject.scmp --preview -v Debug
```

## Library usage (planned)

```csharp
using SqlProjectSync;

var result = SchemaSync.Compare("CompareToProject.scmp");
var publish = SchemaSync.Apply(result, preview: false);

foreach (var added in publish.AddedFiles) { /* ... */ }
```

## License

MIT — see [LICENSE](./LICENSE).
