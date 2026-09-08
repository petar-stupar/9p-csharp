using System.Globalization;
using NineP.Client.Internal;
using NineP.Protocol;
using NineP.Protocol.Internal;
using NineP.Protocol.Messages;
using NineP.Protocol.Negotiation;
using NineP.Protocol.Transports;

namespace NineP.Client;

/// <summary>
/// The entry point: connect a transport, negotiate a dialect, and return a session
/// (architecture §6). Negotiation is one <c>Tversion</c> with <c>NOTAG</c> per dialect on
/// <see cref="ClientOptions.Dialects"/>, and the answer is judged against the offer that drew it
/// (reference §8 rule 18): the client accepts the dialect it just offered, or the plain
/// <c>"9P2000"</c> that version(5) lets a server answer a suffixed offer with — and that one
/// downgrade only as far down as <see cref="ClientOptions.MinDialect"/>. Every other answer is a
/// <see cref="NinePVersionException"/>, a dialect <em>higher</em> than the one offered included:
/// nothing is accepted silently, and "less than it asked for" is accepted only where the caller
/// said so.
/// </summary>
// RS0026: §5.7 fixes these three ConnectAsync overloads, each ending in the trailing
// CancellationToken the repository's async convention requires. The ambiguity the rule guards
// against cannot arise: no two of them accept the same first argument.
#pragma warning disable RS0026
public static class NinePClient
{
    /// <summary>Connects to an address using the transport that owns its scheme, then negotiates.</summary>
    /// <param name="address">Where to connect.</param>
    /// <param name="options">The session configuration.</param>
    /// <param name="cancellationToken">Cancels the dial and the negotiation.</param>
    /// <returns>The negotiated session.</returns>
    /// <exception cref="ArgumentException">No shipped transport owns the address's scheme.</exception>
    /// <exception cref="NinePVersionException">The server refused, or offered too little.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The transfer window is less than 1.</exception>
    public static ValueTask<NinePSession> ConnectAsync(
        NinePAddress address, ClientOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.InFlightWindow, 1, nameof(options.InFlightWindow));
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxReadAll, nameof(options.MaxReadAll));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxReadAll, Array.MaxLength, nameof(options.MaxReadAll));

        return ConnectAsync(TransportFor(address, options), address, options, cancellationToken);
    }

    /// <summary>Connects over a caller-supplied transport: the user-defined-transport seam.</summary>
    /// <param name="transport">The transport to dial with.</param>
    /// <param name="address">Where to connect.</param>
    /// <param name="options">The session configuration.</param>
    /// <param name="cancellationToken">Cancels the dial and the negotiation.</param>
    /// <returns>The negotiated session.</returns>
    /// <exception cref="NinePVersionException">The server refused, or offered too little.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The transfer window is less than 1.</exception>
    public static async ValueTask<NinePSession> ConnectAsync(
        ITransport transport,
        NinePAddress address,
        ClientOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.InFlightWindow, 1, nameof(options.InFlightWindow));
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxReadAll, nameof(options.MaxReadAll));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxReadAll, Array.MaxLength, nameof(options.MaxReadAll));

        using CancellationTokenSource budget =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.ConnectTimeout);

        INinePConnection connection = await transport
            .ConnectAsync(address, budget.Token).ConfigureAwait(false);

        try
        {
            return await NegotiateAsync(connection, options, budget.Token).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Negotiates over a connection the caller already established.</summary>
    /// <param name="connection">The connected transport; the session owns it from here.</param>
    /// <param name="options">The session configuration.</param>
    /// <param name="cancellationToken">Cancels the negotiation.</param>
    /// <returns>The negotiated session.</returns>
    /// <exception cref="NinePVersionException">The server refused, or offered too little.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The transfer window is less than 1.</exception>
    public static async ValueTask<NinePSession> ConnectAsync(
        INinePConnection connection, ClientOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.InFlightWindow, 1, nameof(options.InFlightWindow));
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxReadAll, nameof(options.MaxReadAll));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxReadAll, Array.MaxLength, nameof(options.MaxReadAll));

        using CancellationTokenSource budget =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.ConnectTimeout);

        return await NegotiateAsync(connection, options, budget.Token).ConfigureAwait(false);
    }

    private static ITransport TransportFor(NinePAddress address, ClientOptions options) => address.Scheme switch
    {
        NinePScheme.Tcp => new TcpTransport(),
        NinePScheme.Tls => new TlsTransport(new TlsTransportOptions { Logger = options.Logger }),
        NinePScheme.Ws or NinePScheme.Wss =>
            new WebSocketTransport(new WebSocketTransportOptions { Logger = options.Logger }),

        // A memory endpoint belongs to one MemoryTransport instance and is not routable by name:
        // the caller must hand over the instance that bound it.
        NinePScheme.Memory => throw new ArgumentException(
            "memory:// needs the MemoryTransport instance that bound it; use the ITransport overload",
            nameof(address)),

        _ => throw new ArgumentException(
            string.Format(CultureInfo.InvariantCulture, "no shipped transport dials {0}", address),
            nameof(address)),
    };

    private static async ValueTask<NinePSession> NegotiateAsync(
        INinePConnection connection, ClientOptions options, CancellationToken cancellationToken)
    {
        // CA2000: the multiplexer becomes the session's, and the session is the caller's to
        // dispose; the catch below disposes it on every path that does not return one.
#pragma warning disable CA2000
        TagMultiplexer multiplexer = new(connection, options.Limits, options.Logger, options.RequestTimeout);
#pragma warning restore CA2000
        multiplexer.Start();

        try
        {
            uint requested = options.RequestedMsize();
            (Rversion reply, Dialect offered) = await OfferAsync(multiplexer, options, requested, cancellationToken)
                .ConfigureAwait(false);

            Dialect dialect = Accept(reply, offered, requested, options);
            multiplexer.SetNegotiated(dialect, reply.Msize);

            return new NinePSession(connection, multiplexer, dialect, reply.Msize, options);
        }
        catch
        {
            await multiplexer.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Walks <see cref="ClientOptions.Dialects"/>, which is a preference list and not a single
    /// choice. Reference §5.1: <c>"unknown"</c> refuses the version that was offered, not the
    /// connection — until a version has been agreed the connection accepts nothing but another
    /// <c>Tversion</c> (§8 rule 9), so the next dialect on the list gets its turn. Offering only
    /// the first one meant a <c>.u</c>-only server answered a client whose list read
    /// <c>[.L, .u, 9P2000]</c> with <c>"unknown"</c> and the connect failed.
    /// </summary>
    /// <param name="multiplexer">The multiplexer to negotiate over.</param>
    /// <param name="options">The client's options, whose Dialects list is walked.</param>
    /// <param name="requested">The msize to ask for.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>
    /// The last answer and the dialect that drew it; reference §8 rule 18 judges the one against
    /// the other, so the offer travels out of here with the reply rather than being forgotten.
    /// </returns>
    private static async ValueTask<(Rversion Reply, Dialect Offered)> OfferAsync(
        TagMultiplexer multiplexer, ClientOptions options, uint requested, CancellationToken cancellationToken)
    {
        Rversion reply = default;
        Dialect offered = options.Dialects[0];

        for (int i = 0; i < options.Dialects.Count; i++)
        {
            offered = options.Dialects[i];
            reply = await multiplexer
                .VersionAsync(
                    new Tversion(Constants.NOTAG, requested, Negotiator.VersionString(offered)), cancellationToken)
                .ConfigureAwait(false);

            if (!string.Equals(reply.Version, Constants.VersionUnknown, StringComparison.Ordinal))
            {
                break;
            }
        }

        return (reply, offered);
    }

    private static Dialect Accept(Rversion reply, Dialect offered, uint requested, ClientOptions options)
    {
        if (!Negotiator.TryParseVersion(reply.Version, out Dialect dialect))
        {
            throw new NinePVersionException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "the server answered '{0}', which is not a dialect this client speaks",
                    UntrustedText.Sanitize(reply.Version)),
                reply.Version,
                reply.Msize);
        }

        // Reference §8 rule 18. version(5):64-78 lets a server answer a period-suffixed offer by
        // stripping the suffix, and that is the whole of the licence: the answer is the string
        // that was offered, or "9P2000" when the offer carried a suffix. Anything else is the
        // server answering a question the client did not ask -- a *higher* dialect than was
        // offered most of all, which would run the session in a dialect this side never proposed,
        // and ".u" to a ".L" offer, which is neither the offer nor the base version.
        if (dialect != offered && !(dialect == Dialect.P9_2000 && offered != Dialect.P9_2000))
        {
            throw new NinePVersionException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "the server answered {0} to an offer of {1}; only that string or 9P2000 is an answer to it",
                    reply.Version,
                    Negotiator.VersionString(offered)),
                reply.Version,
                reply.Msize);
        }

        // The suffix-stripping downgrade above is the only one there is, and taking it is a
        // decision the caller made once, in MinDialect; it is never made silently at connect time
        // by whatever the server happened to answer.
        if (dialect < options.MinDialect)
        {
            throw new NinePVersionException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "the server offered {0}, below the configured floor {1}",
                    reply.Version,
                    Negotiator.VersionString(options.MinDialect)),
                reply.Version,
                reply.Msize);
        }

        // version(5) forbids answering with an msize larger than the client asked for, and a
        // client that accepted one would size its buffers from the server's number.
        if (reply.Msize > requested || reply.Msize < options.Limits.MinMsize)
        {
            throw new NinePVersionException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "the server answered msize {0}, outside the {1}..{2} the client offered",
                    reply.Msize,
                    options.Limits.MinMsize,
                    requested),
                reply.Version,
                reply.Msize);
        }

        return dialect;
    }
}
#pragma warning restore RS0026
