namespace NineP.TodoFs.Storage;

/// <summary>A database this build of todofs will not open.</summary>
internal sealed class TodoSchemaException : Exception
{
    /// <summary>Creates an empty refusal.</summary>
    public TodoSchemaException()
    {
    }

    /// <summary>Creates a refusal naming what is wrong with the database.</summary>
    /// <param name="message">Why the database cannot be opened.</param>
    public TodoSchemaException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a refusal wrapping the failure behind it.</summary>
    /// <param name="message">Why the database cannot be opened.</param>
    /// <param name="innerException">The underlying failure.</param>
    public TodoSchemaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
