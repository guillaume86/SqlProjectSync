# HrWebDatabase repro

Throwaway console app that drives `SqlProjectSync` against a real `.scmp`/`.sqlproj` pair so we can reproduce regressions outside the integration-test container.

Default is `--preview` (no writes). Pass `--apply` to actually update the project on disk.

```powershell
dotnet run --project tests/SqlProjectSync.Repros/HrWebDatabase -- "C:\Users\GUI.LECOMTE\source\repos\Mpleo\src\HrWeb.Database\SchemaCompare.user.scmp"
dotnet run --project tests/SqlProjectSync.Repros/HrWebDatabase -- "C:\Users\GUI.LECOMTE\source\repos\Mpleo\src\HrWeb.Database\SchemaCompare.user.scmp" --apply
```

Not added to the `.slnx`; not run in CI.
