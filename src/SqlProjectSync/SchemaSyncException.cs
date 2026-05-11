namespace SqlProjectSync;

/// <summary>
/// Thrown when a schema sync operation fails.
/// </summary>
public class SchemaSyncException : Exception
{
    /// <summary>Creates a new <see cref="SchemaSyncException"/> with the given message.</summary>
    public SchemaSyncException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a new <see cref="SchemaSyncException"/> wrapping the given inner exception.</summary>
    public SchemaSyncException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates a new <see cref="SchemaSyncException"/> aggregating multiple error messages.</summary>
    public SchemaSyncException(string message, IReadOnlyList<string> errors)
        : base(BuildMessage(message, errors))
    {
        Errors = errors;
    }

    /// <summary>The collection of detailed errors associated with this exception, if any.</summary>
    public IReadOnlyList<string> Errors { get; } = Array.Empty<string>();

    private static string BuildMessage(string message, IReadOnlyList<string> errors)
    {
        if (errors.Count == 0)
        {
            return message;
        }

        return $"{message}{Environment.NewLine}{string.Join(Environment.NewLine, errors)}";
    }
}
