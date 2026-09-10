namespace NineP.TodoFs.Storage;

/// <summary>
/// A create the store refused because it would take a user past <see cref="TodoQuotas.MaxLists"/>
/// or a list past <see cref="TodoQuotas.MaxItems"/>. Nothing was written: the refusal is decided
/// and the transaction rolled back before the insert. The handler layer answers it with
/// <c>ENOSPC</c>.
/// </summary>
internal sealed class TodoQuotaException : Exception
{
    /// <summary>Creates an empty refusal.</summary>
    public TodoQuotaException()
    {
    }

    /// <summary>Creates a refusal naming the quota that was reached.</summary>
    /// <param name="message">Which quota refused the create.</param>
    public TodoQuotaException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a refusal wrapping the failure behind it.</summary>
    /// <param name="message">Which quota refused the create.</param>
    /// <param name="innerException">The underlying failure.</param>
    public TodoQuotaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
