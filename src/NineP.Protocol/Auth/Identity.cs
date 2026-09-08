namespace NineP.Protocol.Auth;

/// <summary>
/// Who a session runs as. It is produced by the authenticator and never by the client's claim
/// (workspace architecture §5): a <c>Tattach</c> may say any <c>uname</c> it likes, and the server
/// still runs the session as whoever the afid exchange proved.
/// </summary>
public sealed record Identity
{
    /// <summary>The user name the session runs as.</summary>
    public required string User { get; init; }

    /// <summary>
    /// The numeric uid when one is known; <see cref="Constants.NONUNAME"/> otherwise. The wire
    /// sentinel rather than a nullable, as for every optional id (workspace architecture §12).
    /// </summary>
    public uint Uid { get; init; } = Constants.NONUNAME;

    /// <summary>Group names this identity belongs to.</summary>
    public IReadOnlyList<string> Groups { get; init; } = [];

    /// <summary>
    /// Authenticator-specific claims, for example OIDC token claims. It never contains the raw
    /// token: a claim map is logged and echoed, and a bearer token must be neither.
    /// </summary>
    public IReadOnlyDictionary<string, string> Claims { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// False when nothing was proved and <see cref="User"/> is only what the client claimed — the
    /// identity of a <c>Tattach</c> with <c>afid = NOFID</c>. A filesystem that scopes anything by
    /// the user name must read this first: a server that allows unauthenticated attaches at all
    /// will otherwise hand out a named user's tree to whoever asks for it by name. Only
    /// <see cref="Anonymous"/> produces false, so an identity an authenticator built is
    /// authenticated by construction.
    /// </summary>
    public bool IsAuthenticated { get; private init; } = true;

    /// <summary>The identity of an unauthenticated attach: the uname as claimed, with no groups.</summary>
    /// <param name="user">The name the client claimed.</param>
    /// <param name="uid">The numeric id the client claimed; NONUNAME when it claimed none.</param>
    /// <returns>The identity, which is only as trustworthy as the transport under it.</returns>
    public static Identity Anonymous(string user, uint uid = Constants.NONUNAME) =>
        new() { User = user, Uid = uid, IsAuthenticated = false };
}
