namespace SqlProjectSync;

/// <summary>
/// Controls how <see cref="SchemaSync.Apply"/> reshapes the trailing
/// <c>ALTER TABLE ADD CONSTRAINT</c> statements that
/// <see cref="Microsoft.SqlServer.Dac.Compare.SchemaComparisonResult.PublishChangesToProject(string, Microsoft.SqlServer.Dac.DacExtractTarget)"/>
/// emits for constraints coming from a database source
/// (<see href="https://github.com/microsoft/DacFx/issues/857">DacFx #857</see>).
/// Mirrors DacFx's internal <c>CreateTableInlineConstraintsMode</c>.
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
