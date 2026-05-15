namespace SqlProjectSync;

/// <summary>
/// Controls how <see cref="SchemaSync.Apply"/> reshapes the trailing
/// <c>ALTER TABLE ADD CONSTRAINT</c> statements that
/// <see cref="Microsoft.SqlServer.Dac.Compare.SchemaComparisonResult.PublishChangesToProject(string, Microsoft.SqlServer.Dac.DacExtractTarget)"/>
/// emits as a side-effect of <see href="https://github.com/microsoft/DacFx/issues/792">DacFx #792</see>.
/// Mirrors DacFx's internal <c>CreateTableInlineConstraintsMode</c>.
///
/// Regardless of the selected mode the dedup pass still runs: every standalone
/// <c>ALTER</c> whose constraint name is already declared inline is dropped,
/// because the duplicate produces a model validation error that has to be
/// removed.
/// </summary>
public enum InlineConstraintsMode
{
    /// <summary>
    /// Default. Standalone constraints (no inline twin in <c>CREATE TABLE</c>)
    /// are left where DacFx put them — as trailing <c>ALTER TABLE ADD CONSTRAINT</c>
    /// statements after the <c>CREATE TABLE</c>.
    /// </summary>
    None,

    /// <summary>
    /// Lift every standalone <c>ALTER TABLE ADD CONSTRAINT</c> back into the
    /// matching <c>CREATE TABLE</c>, producing inline-only output. Useful when
    /// migrating an existing project from a tool that emitted inline-form
    /// constraints, so first-sync diffs stay small.
    /// </summary>
    ModelFidelity,
}
