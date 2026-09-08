namespace NineP.Protocol;

/// <summary>
/// Version negotiation failed: the server answered "unknown", or it offered a dialect below the
/// client's floor. The client never silently downgrades (reference §5.1).
/// </summary>
public sealed class NinePVersionException : NinePException
{
    /// <summary>Creates a version exception with no recorded server answer.</summary>
    public NinePVersionException()
        : this("version negotiation failed", Constants.VersionUnknown, 0)
    {
    }

    /// <summary>Creates a version exception with a message.</summary>
    /// <param name="message">The message for the developer.</param>
    public NinePVersionException(string message)
        : base(message)
    {
        ServerVersion = Constants.VersionUnknown;
    }

    /// <summary>Creates a version exception with a message and the failure that caused it.</summary>
    /// <param name="message">The message for the developer.</param>
    /// <param name="innerException">The failure being wrapped.</param>
    public NinePVersionException(string message, Exception innerException)
        : base(message, innerException)
    {
        ServerVersion = Constants.VersionUnknown;
    }

    /// <summary>Creates a version exception recording what the server answered.</summary>
    /// <param name="message">The message for the developer.</param>
    /// <param name="serverVersion">The version string the server replied with.</param>
    /// <param name="serverMsize">The msize the server replied with.</param>
    public NinePVersionException(string message, string serverVersion, uint serverMsize)
        : base(message)
    {
        ServerVersion = serverVersion;
        ServerMsize = serverMsize;
    }

    /// <summary>The version string the server replied with, for example "unknown".</summary>
    public string ServerVersion { get; }

    /// <summary>The msize the server replied with.</summary>
    public uint ServerMsize { get; }
}
