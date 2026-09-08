using NineP.Protocol;
using NineP.Protocol.Auth;

namespace NineP.Server;

/// <summary>
/// One completed request, for auditing (architecture §4). Every string in it has already been
/// escaped and capped by the core (reference §8 rule 11), so a sink may write it out as it is.
/// </summary>
/// <param name="Identity">Who the request ran as, or null before an attach.</param>
/// <param name="Request">The T-message type.</param>
/// <param name="Summary">A short, sanitised description of what was asked.</param>
/// <param name="Reply">The R-message type that answered it.</param>
/// <param name="Error">The error value when the reply was an error.</param>
/// <param name="Duration">How long the request took.</param>
public readonly record struct RequestLogEntry(
    Identity? Identity,
    MessageType Request,
    string Summary,
    MessageType Reply,
    NinePError? Error,
    TimeSpan Duration);
