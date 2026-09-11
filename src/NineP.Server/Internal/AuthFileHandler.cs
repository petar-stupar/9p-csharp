using NineP.Protocol;
using NineP.Protocol.Auth;

namespace NineP.Server.Internal;

/// <summary>
/// The file an afid names (reference §5.2). It exists so that a fid has a qid with
/// <c>QTAUTH</c> set and so that the <c>Tread</c> and <c>Twrite</c> a client aims at the afid
/// reach the <see cref="IAuthSession"/> the authenticator started.
/// </summary>
/// <remarks>
/// The exchange is bounded in both directions, because an afid is reachable before anything has
/// been authenticated: a peer that never finishes the exchange must not be able to make the
/// server buffer without limit or hold a connection open for ever. Both bounds come from
/// <see cref="Limits"/> — <see cref="Limits.MaxAuthBytes"/> per direction and
/// <see cref="Limits.AuthTimeout"/> of wall-clock time, the latter disabled by
/// <see cref="TimeSpan.Zero"/> as <see cref="Limits.IdleTimeout"/> is. Exceeding either is
/// <c>"authentication failed"</c> / <c>EACCES</c>: the exchange did not succeed, and the client
/// learns nothing else about why.
/// </remarks>
internal sealed class AuthFileHandler : IHandler
{
    private readonly IAuthSession _exchange;
    private readonly Limits _limits;
    private readonly TimeProvider _clock;
    private readonly long _started;
    private int _written;
    private int _read;

    /// <summary>Wraps one in-progress exchange.</summary>
    /// <param name="exchange">The session the authenticator returned.</param>
    /// <param name="limits">The bounds the exchange runs under.</param>
    /// <param name="clock">The clock the deadline is measured on.</param>
    public AuthFileHandler(IAuthSession exchange, Limits limits, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(clock);

        _exchange = exchange;
        _limits = limits;
        _clock = clock;
        _started = clock.GetTimestamp();
    }

    /// <summary>The qid every afid carries: <c>QTAUTH</c>, so a client can tell it apart.</summary>
    public Qid Qid => new(QidType.QTAUTH, 0, 0);

    /// <summary>Accepts credential bytes from the client, within the byte and time bounds.</summary>
    /// <param name="data">The bytes the client wrote to the afid.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The number of bytes accepted, which is always all of them or none.</returns>
    /// <exception cref="NinePException">A bound was exceeded, or the authenticator refused.</exception>
    public async ValueTask<int> WriteAsync(
        ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_written > _limits.MaxAuthBytes - data.Length)
        {
            throw Failed();
        }

        using CancellationTokenSource deadline = Deadline();
        using CancellationTokenSource bounded =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await _exchange.WriteAsync(data, bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failed();
        }

        _written += data.Length;
        return data.Length;
    }

    /// <summary>Produces the server's half of the exchange, within the byte and time bounds.</summary>
    /// <param name="maxBytes">The most bytes the reply can carry.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes to answer with; empty means the exchange has nothing more to say.</returns>
    /// <exception cref="NinePException">A bound was exceeded, or the authenticator refused.</exception>
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(
        int maxBytes, CancellationToken cancellationToken)
    {
        if (_read >= _limits.MaxAuthBytes)
        {
            throw Failed();
        }

        int budget = Math.Min(maxBytes, _limits.MaxAuthBytes - _read);

        using CancellationTokenSource deadline = Deadline();
        using CancellationTokenSource bounded =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        ReadOnlyMemory<byte> answer;
        try
        {
            answer = await _exchange.ReadAsync(budget, bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failed();
        }

        // A refusal to honour the budget is the authenticator's bug, not the client's: the reply
        // would not fit the frame, so the exchange ends rather than the connection.
        if (answer.Length > budget)
        {
            throw Failed();
        }

        _read += answer.Length;
        return answer;
    }

    /// <summary>The afid's attributes: a plain file that is neither readable nor writable by mode.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The attributes of the auth file.</returns>
    public ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new Attr
        {
            Qid = Qid,
            Kind = FileKind.File,
            Perm = FilePermissions.OwnerReadWrite,
            Flags = FileFlags.Auth,
        });

    /// <summary>An afid has no attributes to change.</summary>
    /// <param name="update">Ignored.</param>
    /// <param name="cancellationToken">Ignored.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NinePException">Always: an afid is not a file to be changed.</exception>
    public ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default) =>
        throw new NinePException(NinePError.FromErrno(Errno.EPERM));

    /// <summary>Releasing the afid ends the exchange; the session itself is disposed with the entry.</summary>
    /// <param name="wasOpen">Ignored: an afid is never opened.</param>
    /// <param name="cancellationToken">Ignored.</param>
    /// <returns>A completed task.</returns>
    public ValueTask ClunkAsync(bool wasOpen, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <summary>An afid has nothing to flush.</summary>
    /// <param name="dataOnly">Ignored.</param>
    /// <param name="cancellationToken">Ignored.</param>
    /// <returns>A completed task.</returns>
    public ValueTask FsyncAsync(bool dataOnly, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    private static NinePException Failed() =>
        new(NinePError.FromEname("authentication failed"));

    /// <summary>
    /// A source that fires when the exchange's wall-clock budget is spent, measured on the
    /// injected clock so that a test does not have to wait out a real thirty seconds.
    /// </summary>
    /// <returns>A source that never fires when the timeout is disabled.</returns>
    private CancellationTokenSource Deadline()
    {
        if (_limits.AuthTimeout <= TimeSpan.Zero)
        {
            return new CancellationTokenSource();
        }

        TimeSpan remaining = _limits.AuthTimeout - _clock.GetElapsedTime(_started);
        return new CancellationTokenSource(
            remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, _clock);
    }
}
