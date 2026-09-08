using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Channels;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using NineP.Protocol.Negotiation;
using NineP.Protocol.Transports;

namespace NineP.Server.Internal;

/// <summary>
/// One connection's 9P session (architecture §4): one reader task, one writer task, and the
/// version rules of reference §5.1 and §8 rule 9 between them. Replies are serialised through a
/// single bounded channel because <c>SslStream</c> and <c>WebSocket</c> both forbid concurrent
/// writes (RK-58), and because a 9P frame goes out whole or the connection is desynchronised.
/// </summary>
internal sealed class ServerSession : IAsyncDisposable
{
    /// <summary>The ename a peer is told when it speaks before a dialect has been agreed.</summary>
    public const string VersionNotNegotiated = "version not negotiated";

    /// <summary>
    /// The ename of reference §2 for a message the negotiated dialect does not carry; it projects
    /// to <c>EOPNOTSUPP</c>, which is what a <c>.L</c> peer is told.
    /// </summary>
    public const string UnknownMessage = "unknown message";

    private readonly INinePConnection _connection;
    private readonly ServerOptions _options;
    private readonly IFilesystem _filesystem;
    private readonly Limits _limits;
    private readonly ServerMetrics _metrics;
    private readonly Pipe _inbound = new();
    private readonly FrameReader _frames;
    private readonly Channel<PendingReply> _replies;
    private readonly FidTable _fids;
    private readonly TagTable _tags = new();
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _listenerInFlight;
    private readonly SemaphoreSlim _general;
    private readonly SemaphoreSlim _flushSlots;
    private readonly WorkerCounter _workers = new();
    private readonly SemaphoreSlim _replyGate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();

    // Captured once: the token stays usable after the source has been disposed, and by then
    // it has already been cancelled, so every wait short-circuits instead of registering.
    private readonly CancellationToken _stoppingToken;
    private Task _writing = Task.CompletedTask;
    private Task _filling = Task.CompletedTask;
    private int _disposed;
    private Task _cleanup = Task.CompletedTask;
    private readonly TaskCompletionSource _runFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _runState;

    /// <summary>Creates a session over an accepted connection.</summary>
    /// <param name="connection">The accepted connection; the session owns it.</param>
    /// <param name="options">The server configuration.</param>
    /// <param name="filesystem">The tree being served.</param>
    /// <param name="metrics">The counters this connection contributes to.</param>
    /// <param name="openState">The server-wide open state, which owns the DMEXCL registry.</param>
    /// <param name="listenerInFlight">The listener-wide in-flight budget (reference §8 rule 8).</param>
    public ServerSession(
        INinePConnection connection,
        ServerOptions options,
        IFilesystem filesystem,
        ServerMetrics metrics,
        OpenState openState,
        SemaphoreSlim? listenerInFlight = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(filesystem);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(openState);

        _stoppingToken = _stopping.Token;
        _connection = connection;
        _options = options;
        _filesystem = filesystem;
        _limits = options.Limits;
        _metrics = metrics;
        _fids = new FidTable(options.Limits.MaxFidsPerConnection, openState.Paths);
        _dispatcher = new Dispatcher(this, openState);

        // Reference §8 rule 8: two budgets per connection, because backpressure is per connection
        // and would otherwise stop Tflush being read as well — a client that filled its window
        // could then never cancel anything in it.
        GeneralCapacity = options.Limits.MaxInFlightPerConnection - options.Limits.FlushReservePerConnection;
        _general = new SemaphoreSlim(GeneralCapacity, GeneralCapacity);
        _flushSlots = new SemaphoreSlim(
            options.Limits.FlushReservePerConnection, options.Limits.FlushReservePerConnection);
        _listenerInFlight = listenerInFlight ?? new SemaphoreSlim(options.Limits.MaxInFlightPerListener);
        _frames = new FrameReader(_inbound.Reader, options.Limits, ArrayPool<byte>.Shared);
        _replies = Channel.CreateBounded<PendingReply>(new BoundedChannelOptions(options.Limits.MaxInFlightPerConnection)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>The dialect this session negotiated; 9P2000 until one has been agreed.</summary>
    public Dialect Dialect { get; private set; } = Dialect.P9_2000;

    /// <summary>The msize both sides agreed on; zero before negotiation.</summary>
    public uint Msize { get; private set; }

    /// <summary>True once a dialect has been agreed (reference §8 rule 9).</summary>
    public bool IsNegotiated { get; private set; }

    /// <summary>The largest payload one reply may carry: msize - IOHDRSZ.</summary>
    public int MaxPayload => (int)Msize - Constants.IOHDRSZ;

    /// <summary>How many times this session has been reset by a mid-session <c>Tversion</c>.</summary>
    public int Generation { get; private set; }

    /// <summary>The tree this session serves.</summary>
    public IFilesystem Filesystem => _filesystem;

    /// <summary>The configuration this session runs under.</summary>
    public ServerOptions Options => _options;

    /// <summary>This connection's fid table (reference §8 rule 7).</summary>
    public FidTable Fids => _fids;

    /// <summary>This connection's tag table (reference §8 rule 6).</summary>
    public TagTable Tags => _tags;

    /// <summary>What the transport learned about the peer, for an authenticator that wants it.</summary>
    public PeerIdentity? PeerIdentity => _connection.PeerIdentity;

    /// <summary>Requests other than <c>Tflush</c> this connection may have in flight at once.</summary>
    public int GeneralCapacity { get; }

    /// <summary>General slots still free; excess ordinary requests receive EAGAIN.</summary>
    public int GeneralAvailable => _general.CurrentCount;

    /// <summary>Serves the connection until the peer closes it or the token fires.</summary>
    /// <param name="cancellationToken">Stops the session.</param>
    /// <returns>A task that completes when the session has stopped.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _runState, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await RunCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runFinished.TrySetResult();
        }
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stoppingToken);

        _filling = Task.Run(() => FillAsync(linked.Token), CancellationToken.None);
        _writing = Task.Run(WriteLoopAsync, CancellationToken.None);

        try
        {
            await ReadLoopAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The server is shutting down; the connection is closed below like any other end.
        }
        finally
        {
            // Nothing may still be writing when the channel is completed, or a reply would be
            // dropped after its handler had already committed to it.
            _tags.Clear();
            await DrainRequestsAsync().ConfigureAwait(false);
            _replies.Writer.TryComplete();
            await _writing.ConfigureAwait(false);
        }
    }

    /// <summary>Encodes one reply and hands it to the connection's single writer.</summary>
    /// <typeparam name="TMessage">The reply record.</typeparam>
    /// <param name="message">The reply.</param>
    /// <param name="dialect">The dialect to encode in; <c>Rversion</c> is always 9P2000.</param>
    /// <returns>A task that completes once the reply is queued.</returns>
    /// <exception cref="NinePException">The reply cannot be encoded.</exception>
    public ValueTask EnqueueAsync<TMessage>(TMessage message, Dialect dialect)
        where TMessage : struct, IMessage =>
        SendAsync(Encode(in message, dialect));

    /// <summary>Answers one request with the error shape the session dialect calls for (§6.3).</summary>
    /// <param name="tag">The tag being answered.</param>
    /// <param name="error">The error value.</param>
    /// <returns>A task that completes once the reply is queued.</returns>
    public ValueTask EnqueueErrorAsync(ushort tag, NinePError error)
    {
        _metrics.CountError(error);

        return SendAsync(EncodeError(tag, error));
    }

    /// <summary>
    /// Encodes one reply into a rental from the shared pool. Encoding is where a reply can still
    /// be refused — <c>StatCodec</c> throws <c>NinePException(EOVERFLOW)</c> for a stat record too
    /// long for its own <c>size[2]</c> — so it is deliberately separable from the queueing: the
    /// paths that answer a tag encode <b>before</b> they claim it.
    /// </summary>
    /// <typeparam name="TMessage">The reply record.</typeparam>
    /// <param name="message">The reply.</param>
    /// <param name="dialect">The dialect to encode in.</param>
    /// <returns>The encoded frame, in a buffer the writer returns to the pool.</returns>
    /// <exception cref="NinePException">The reply cannot be encoded.</exception>
    private static PendingReply Encode<TMessage>(in TMessage message, Dialect dialect)
        where TMessage : struct, IMessage
    {
        int size = MessageCodec.GetEncodedSize(in message, dialect);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);

        try
        {
            ArraySegmentWriter writer = new(buffer);
            MessageCodec.Encode(writer, in message, dialect);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }

        return new PendingReply(buffer, size);
    }

    /// <summary>
    /// Encodes the error reply this dialect calls for. It cannot itself fail to encode:
    /// <c>ErrorProjector.ToRerror</c> truncates the ename to <c>ERRMAX - 1</c> and an
    /// <c>Rlerror</c> is a fixed eleven bytes, so this is the answer a request can always be
    /// given — which is what makes encode-before-CAS a complete fix rather than half of one.
    /// </summary>
    /// <param name="tag">The tag being answered.</param>
    /// <param name="error">The error value.</param>
    /// <returns>The encoded frame.</returns>
    private PendingReply EncodeError(ushort tag, NinePError error) =>
        Dialect == Dialect.P9_2000_L
            ? Encode(ErrorProjector.ToRlerror(tag, error), Dialect)
            : Encode(ErrorProjector.ToRerror(tag, error, Dialect), Dialect);

    /// <summary>Hands an already-encoded reply to the connection's single writer.</summary>
    /// <param name="reply">The frame and the rental holding it.</param>
    /// <returns>A task that completes once the reply is queued.</returns>
    private async ValueTask SendAsync(PendingReply reply)
    {
        try
        {
            await _replies.Writer.WriteAsync(reply, _stoppingToken).ConfigureAwait(false);
        }
        catch
        {
            // The channel was completed, or the session is being disposed: these bytes will never
            // reach the wire, so the buffer goes back rather than leaking out of the pool.
            ArrayPool<byte>.Shared.Return(reply.Buffer);
            throw;
        }
    }

    /// <summary>Stops the session and closes the connection.</summary>
    /// <returns>A task that completes when the reader and writer have stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _runState, 2, 0) == 0)
        {
            _runFinished.TrySetResult();
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        _replies.Writer.TryComplete();

        try
        {
            await Task.WhenAll(_filling, _writing).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is OperationCanceledException or IOException
            or ObjectDisposedException or NinePException)
        {
            // The connection is being torn down; how the loops ended is not news.
        }

        _tags.Clear();
        await _connection.DisposeAsync().ConfigureAwait(false);

        // A handler may ignore cancellation. Stop the transport immediately, but retain its fid
        // and synchronization resources until its operation lease is returned. Cleanup is bounded
        // by the number of admitted requests and observes/logs every handler cleanup failure.
        _cleanup = CleanupAsync();
        if (_cleanup.IsCompleted)
        {
            await _cleanup.ConfigureAwait(false);
        }
    }

    private async Task CleanupAsync()
    {
        try
        {
            await _runFinished.Task.ConfigureAwait(false);
            await DrainRequestsAsync(waitForHandlers: true).ConfigureAwait(false);
            await _dispatcher.ClearAsync(CancellationToken.None).ConfigureAwait(false);
            await _fids.DisposeAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // This task may outlive DisposeAsync; observe every failure at the logger boundary.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            _options.Logger.CleanupFailedSafely(failure);
        }
        finally
        {
            _general.Dispose();
            _flushSlots.Dispose();
            _replyGate.Dispose();
            _stopping.Dispose();
        }
    }

    /// <summary>
    /// Handles one decoded request. Task 27 owns <c>Tversion</c> and the pre-negotiation rule; the
    /// fid, tag and handler machinery of tasks 28 to 32 hangs off the same call.
    /// </summary>
    /// <param name="type">The type peeked out of the frame.</param>
    /// <param name="frame">The complete frame.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>False when the connection must close after the reply.</returns>
    private async ValueTask<bool> HandleAsync(
        MessageType type, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        _metrics.CountRequest(type);

        if (type == MessageType.Tversion)
        {
            await VersionAsync(frame, cancellationToken).ConfigureAwait(false);
            return true;
        }

        // Reference §8 rule 9: before a dialect is agreed there is no agreed error type, so the
        // one form every 9P peer can decode is used and the connection is then closed.
        if (!IsNegotiated)
        {
            ushort tag = MessageCodec.PeekTag(frame.Span);
            await EnqueueAsync(new Rerror(tag, VersionNotNegotiated, 0), Dialect.P9_2000)
                .ConfigureAwait(false);
            return false;
        }

        // Reference §2: a type the negotiated dialect does not carry never reaches a handler, and
        // it draws Rerror "unknown message" / Rlerror EOPNOTSUPP — not a close. The frame parsed;
        // the stream behind it is still trustworthy, so there is nothing to resync from and
        // u9fs and plan9 both stay up. The close is reserved for the framing violations of §8
        // rule 2, where there is no way to tell where the next message begins.
        if (!MessageTypes.IsLegal(type, Dialect) || !MessageTypes.IsRequest(type))
        {
            await EnqueueErrorAsync(
                MessageCodec.PeekTag(frame.Span), NinePError.FromEname(UnknownMessage))
                .ConfigureAwait(false);
            return true;
        }

        // §6.6 rule 6: Tflush is answered inline, out of its own reserve. Handling it here rather
        // than on a worker is what keeps it in arrival order relative to the request it names.
        if (type == MessageType.Tflush)
        {
            await FlushAsync(frame, cancellationToken).ConfigureAwait(false);
            return true;
        }

        await AdmitAsync(type, frame, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Takes a slot from both budgets without blocking the reader. Excess ordinary requests
    /// receive EAGAIN, so a subsequent Tflush can still reach its reserved capacity.
    /// </summary>
    /// <param name="type">The T-message type.</param>
    /// <param name="frame">The complete frame, which is copied for the worker.</param>
    /// <param name="cancellationToken">Stops the session.</param>
    /// <returns>A task that completes once the request has been admitted and started.</returns>
    private async ValueTask AdmitAsync(
        MessageType type, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        ushort tag = MessageCodec.PeekTag(frame.Span);

        PendingRequest pending;
        try
        {
            pending = _tags.Begin(tag, type);
        }
        catch (NinePException duplicate)
        {
            // Reference §8 rule 6: the pending request keeps the tag and is untouched; it is the
            // new request that is dropped, because answering both would give the client two
            // replies it cannot tell apart.
            await EnqueueErrorAsync(tag, duplicate.Error).ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!_general.Wait(0, CancellationToken.None))
        {
            await CompleteAsync(pending, NinePError.FromErrno(Errno.EAGAIN)).ConfigureAwait(false);
            return;
        }

        if (!_listenerInFlight.Wait(0, CancellationToken.None))
        {
            _general.Release();
            await CompleteAsync(pending, NinePError.FromErrno(Errno.EAGAIN)).ConfigureAwait(false);
            return;
        }

        pending.HoldBudget();

        // The frame is copied because the read loop advances the pipe past it before the worker
        // runs, and Twrite.Data is a view of the frame that the handler still holds.
        byte[] copy = ArrayPool<byte>.Shared.Rent(frame.Length);
        frame.CopyTo(copy);

        // Queue the fid lease in arrival order. Dispatcher yields after acquiring it, before
        // invoking any handler, so synchronous handler work cannot block this read loop. The
        // worker is counted before its task exists, so a drain that starts in between waits for it.
        _workers.Enter();
        _ = WorkAsync(type, copy, frame.Length, pending, cancellationToken);
    }

    private async Task WorkAsync(
        MessageType type,
        byte[] frame,
        int length,
        PendingRequest pending,
        CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource work =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, pending.Cts.Token);

            await _dispatcher.HandleAsync(type, frame.AsMemory(0, length), pending, work.Token)
                .ConfigureAwait(false);
        }
        catch (NinePProtocolException malformed)
        {
            // Reference §8 rule 2: the reply goes out and the connection is then closed. A frame
            // whose counted field disagreed with its own size leaves everything after it in the
            // stream untrustworthy, so there is nothing to resync to.
            _metrics.CountProtocolError(malformed.Kind);
            await CompleteAsync(pending, NinePError.FromErrno(Errno.EPROTO)).ConfigureAwait(false);
            await DrainAsync().ConfigureAwait(false);
            await CloseAsync(CloseReason.ProtocolViolation, null).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is OperationCanceledException or ObjectDisposedException)
        {
            // The session is going away, or a flush cancelled this request.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);

            // The backstop: it releases by identity, so it cannot evict whatever has claimed the
            // tag since the reply was queued or the Tflush freed it, and it is where the request's
            // cancellation source is finally disposed -- the flush path leaves it alive precisely
            // because this handler was still running under it. The budgets went back when the
            // reply was queued; this returns them only for a request that never got one, which is
            // a flushed request whose cancelled handler has now unwound.
            _tags.Release(pending);
            ReleaseBudget(pending);
            _workers.Exit();
        }
    }

    /// <summary>
    /// Returns the two in-flight budgets of reference §8 rule 8, once. It runs under the reply
    /// gate as the reply is queued, for the same reason the tag is freed there: the gate does not
    /// hold the write loop, so the reply can be on the wire before the worker's <c>finally</c>
    /// runs, and a client that sends its next request the instant it has the reply would be
    /// refused <c>EAGAIN</c> for a window it has already been given back. With a general window of
    /// one, a fast peer saw exactly that.
    /// </summary>
    /// <param name="pending">The request whose budgets are returned.</param>
    private void ReleaseBudget(PendingRequest pending)
    {
        if (pending.TryReleaseBudget())
        {
            _listenerInFlight.Release();
            _general.Release();
        }
    }

    /// <summary>
    /// flush(5) and §6.6: <c>Rflush</c> is sent in every case — an unknown tag, an already
    /// answered one, and a <c>Tflush</c> flushing another <c>Tflush</c> are all answered with it
    /// and never with an error. When this flush is the one that wins the CAS, <c>oldtag</c> is
    /// free <b>before</b> the <c>Rflush</c> goes out, because that is the instant reference §5.3
    /// lets the client reuse it.
    /// </summary>
    /// <param name="frame">The flush frame.</param>
    /// <param name="cancellationToken">Stops the session.</param>
    /// <returns>A task that completes once the Rflush is queued.</returns>
    private async ValueTask FlushAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        Tflush request = MessageCodec.Decode<Tflush>(frame, Dialect);

        await _flushSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The gate makes the CAS and the enqueue one step, so a reply can never be queued
            // after the Rflush that suppressed it. Tags.Flush also frees oldtag when it is the
            // call that suppressed the reply, which must happen before the Rflush is queued:
            // reference §5.3 lets the client reuse oldtag the moment it has the Rflush, and a
            // tag still in the table refuses that legal reuse as a duplicate (§8 rule 6).
            await _replyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _tags.Flush(request.OldTag);
                await EnqueueAsync(new Rflush(request.Tag), Dialect).ConfigureAwait(false);
            }
            finally
            {
                _replyGate.Release();
            }
        }
        finally
        {
            _flushSlots.Release();
        }
    }

    /// <summary>
    /// Claims the right to answer a request and queues the reply in one step (§6.6). The tag is
    /// freed here, under the same gate, and not in the worker's <c>finally</c> — and it is freed
    /// <b>before</b> the reply is queued, which is the whole point of the order below; the two
    /// in-flight budgets go back at the same point and for the same reason
    /// (<see cref="ReleaseBudget"/>). A client
    /// may reuse a tag the moment it has the reply (reference §5.3, and Linux v9fs does so on
    /// every request); the reply gate does not hold the write loop, so between an enqueue and a
    /// release the reply can already be on the wire and the client's next, legal request would
    /// draw <c>"duplicate tag"</c> and be dropped (§8 rule 6). Releasing first cannot let a reply
    /// out after its own <c>Rflush</c>: that invariant is carried by <c>TryComplete</c>'s CAS and
    /// by this gate, which <c>FlushAsync</c> also takes. The worker still releases as a backstop
    /// for the paths that queue no reply at all; <see cref="TagTable.Release"/> removes by
    /// identity, so that late backstop cannot evict whatever has claimed the tag since.
    /// <para>
    /// The reply is <b>encoded before the CAS is claimed</b>, and outside the gate, because the
    /// encode needs neither and because it can fail: a stat record whose <c>name</c>, <c>uid</c>,
    /// <c>gid</c>, <c>muid</c> or .u <c>extension</c> push it past its own 16-bit <c>size[2]</c>
    /// is refused with <c>NinePException(EOVERFLOW)</c>, and a client can provoke exactly that by
    /// attaching with a 30 000-byte <c>uname</c> to a tree that reports the owner it was told.
    /// Claiming the tag first left such a request answered by nobody — no frame on the wire, the
    /// tag freed, and the audit hook told it had been answered <c>Rstat</c>. Encoding first makes
    /// it an ordinary <c>NinePException</c> that <c>Dispatcher.HandleAsync</c> turns into the
    /// error reply the client is owed.
    /// </para>
    /// </summary>
    /// <typeparam name="TMessage">The reply record.</typeparam>
    /// <param name="pending">The tag's state.</param>
    /// <param name="message">The reply.</param>
    /// <returns>A task that completes once the reply is queued or suppressed.</returns>
    /// <exception cref="NinePException">The reply cannot be encoded; nothing has been claimed.</exception>
    public async ValueTask CompleteAsync<TMessage>(PendingRequest pending, TMessage message)
        where TMessage : struct, IMessage
    {
        ArgumentNullException.ThrowIfNull(pending);

        PendingReply reply = Encode(in message, Dialect);
        bool queued = false;

        await _replyGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (pending.TryComplete())
            {
                pending.Reply = TMessage.Type;
                _tags.Release(pending);
                ReleaseBudget(pending);
                queued = true;
                await SendAsync(reply).ConfigureAwait(false);
            }
        }
        finally
        {
            _replyGate.Release();

            if (!queued)
            {
                // A Tflush claimed the request first, so these bytes never go out.
                ArrayPool<byte>.Shared.Return(reply.Buffer);
            }
        }
    }

    /// <summary>Claims the right to answer a request with an error and queues it in one step.</summary>
    /// <param name="pending">The tag's state.</param>
    /// <param name="error">The error value.</param>
    /// <returns>A task that completes once the reply is queued or suppressed.</returns>
    public async ValueTask CompleteAsync(PendingRequest pending, NinePError error)
    {
        ArgumentNullException.ThrowIfNull(pending);

        PendingReply reply = EncodeError(pending.Tag, error);
        bool queued = false;

        await _replyGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (pending.TryComplete())
            {
                pending.Reply = ErrorProjector.ErrorTypeFor(Dialect);
                pending.Error = error;
                _metrics.CountError(error);
                _tags.Release(pending);
                ReleaseBudget(pending);
                queued = true;
                await SendAsync(reply).ConfigureAwait(false);
            }
        }
        finally
        {
            _replyGate.Release();

            if (!queued)
            {
                ArrayPool<byte>.Shared.Return(reply.Buffer);
            }
        }
    }

    private async ValueTask VersionAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        // Tversion is decoded and answered in the base dialect: it is what decides the dialect,
        // so it cannot be read in one.
        Tversion request = MessageCodec.Decode<Tversion>(frame, Dialect.P9_2000);
        NegotiationResult answer = Negotiator.Negotiate(
            _options.Dialects, request.Version, request.Msize, _limits);

        await ResetAsync(cancellationToken).ConfigureAwait(false);

        if (answer.Dialect is not Dialect agreed)
        {
            // version(5): a refusal is Rversion "unknown" echoing the client's msize, never an
            // Rerror, and the connection stays open for another Tversion only.
            IsNegotiated = false;
            Msize = 0;
            Dialect = Dialect.P9_2000;
            _frames.ResetToPreNegotiation();
            await EnqueueAsync(new Rversion(request.Tag, answer.Msize, answer.Version), Dialect.P9_2000)
                .ConfigureAwait(false);
            return;
        }

        Dialect = agreed;
        Msize = answer.Msize;
        IsNegotiated = true;
        _frames.SetNegotiatedMsize(answer.Msize);

        await EnqueueAsync(new Rversion(request.Tag, answer.Msize, answer.Version), Dialect.P9_2000)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A successful <c>Tversion</c> is a new session: reference §5.1 aborts every outstanding
    /// request and clunks every fid, so nothing from before it can be referred to afterwards.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reset.</param>
    /// <returns>A task that completes when the previous session has been torn down.</returns>
    private async ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        Generation++;

        // §5.1: a successful Tversion aborts all outstanding I/O and clunks every fid. The
        // in-flight work is cancelled and waited for, so no handler outlives the session it
        // belonged to and writes into the next one.
        _tags.Clear();
        await DrainRequestsAsync().ConfigureAwait(false);
        await _dispatcher.ClearAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits until nothing is running: every general slot is free again.</summary>
    /// <param name="waitForHandlers">True to defer final resource disposal until every handler returns.</param>
    /// <returns>A task that completes when the connection is idle.</returns>
    private async ValueTask DrainRequestsAsync(bool waitForHandlers = false)
    {
        int taken = 0;

        try
        {
            CancellationToken waiting = waitForHandlers ? CancellationToken.None : _stoppingToken;

            for (; taken < GeneralCapacity; taken++)
            {
                await _general.WaitAsync(waiting).ConfigureAwait(false);
            }

            // Every slot held means nothing is unanswered and nothing new is admitted; the budgets
            // go back when a reply is queued, though, so the workers are waited for separately.
            await _workers.WhenIdleAsync().WaitAsync(waiting).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Disposal. A handler still holding a slot may be parked on a reply that can no longer
            // be written, so the drain gives up rather than waiting for something that will not
            // happen; nothing it produces can reach the wire after this point anyway.
        }
        finally
        {
            if (taken > 0)
            {
                _general.Release(taken);
            }
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            FrameReadResult result = await _frames.ReadFrameAsync(cancellationToken).ConfigureAwait(false);

            if (result.Failure is not null || result.IsEndOfStream || !result.HasFrame)
            {
                await CloseAsync(result.Close ?? CloseReason.PeerClosed, result.Failure).ConfigureAwait(false);
                return;
            }

            using FrameLease lease = result.Lease!;
            MessageType type = MessageCodec.PeekType(lease.Frame.Span);

            if (!await SafeHandleAsync(type, lease.Frame, cancellationToken).ConfigureAwait(false))
            {
                await DrainAsync().ConfigureAwait(false);
                await CloseAsync(CloseReason.ProtocolViolation, null).ConfigureAwait(false);
                return;
            }
        }
    }

    private async ValueTask<bool> SafeHandleAsync(
        MessageType type, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        try
        {
            return await HandleAsync(type, frame, cancellationToken).ConfigureAwait(false);
        }
        catch (NinePProtocolException malformed)
        {
            // Reference §8 rule 2: a malformed message is answered and the connection is closed;
            // there is no way to tell how much of the stream after it is still trustworthy.
            _metrics.CountProtocolError(malformed.Kind);
            await EnqueueErrorAsync(MessageCodec.PeekTag(frame.Span), NinePError.FromErrno(Errno.EPROTO))
                .ConfigureAwait(false);
            return false;
        }
    }

    private async Task FillAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                Memory<byte> destination = _inbound.Writer.GetMemory(4096);
                int read = await _connection.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                _metrics.AddBytesRead(read);
                _inbound.Writer.Advance(read);

                FlushResult flush = await _inbound.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (flush.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception failure) when (failure is OperationCanceledException or IOException
            or ObjectDisposedException or NinePException)
        {
            // The peer went away; the read loop turns that into a close.
        }
        finally
        {
            await _inbound.Writer.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The connection's single writer. Every wait in it observes the session's stopping token,
    /// because a peer that has stopped reading blocks the write indefinitely — a full socket buffer,
    /// or a full pipe in process — and <see cref="DisposeAsync"/> waits for this loop. Without the
    /// token a server could not be shut down while such a peer was attached: the write never
    /// returns, the disposal never completes, and nothing in the process is burning CPU while it
    /// does not.
    /// </summary>
    /// <returns>A task that completes when the writer has stopped.</returns>
    private async Task WriteLoopAsync()
    {
        CancellationToken stopping = _stoppingToken;

        try
        {
            while (await _replies.Reader.WaitToReadAsync(stopping).ConfigureAwait(false))
            {
                while (_replies.Reader.TryRead(out PendingReply reply))
                {
                    try
                    {
                        await _connection
                            .WriteAsync(reply.Buffer.AsMemory(0, reply.Length), stopping)
                            .ConfigureAwait(false);
                        _metrics.AddBytesWritten(reply.Length);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(reply.Buffer);
                    }
                }
            }
        }
        catch (Exception failure) when (failure is IOException or ObjectDisposedException
            or NinePException or OperationCanceledException)
        {
            // The peer went away mid-reply, or the session is being disposed under a peer that
            // stopped reading; either way nothing still queued can reach the wire.
            DrainQueued();
        }
    }

    /// <summary>Waits until everything already queued has reached the wire.</summary>
    /// <returns>A task that completes when the writer has drained.</returns>
    private async ValueTask DrainAsync()
    {
        _replies.Writer.TryComplete();
        await _writing.ConfigureAwait(false);
    }

    private void DrainQueued()
    {
        while (_replies.Reader.TryRead(out PendingReply reply))
        {
            ArrayPool<byte>.Shared.Return(reply.Buffer);
        }
    }

    private async ValueTask CloseAsync(CloseReason reason, ProtocolErrorKind? failure)
    {
        if (failure is ProtocolErrorKind kind)
        {
            _metrics.CountProtocolError(kind);
            _options.Logger.FramingViolation(kind);
        }

        await _connection.CloseAsync(reason, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>An <see cref="IBufferWriter{T}"/> over one rented array, so a reply is encoded once.</summary>
    private sealed class ArraySegmentWriter(byte[] buffer) : IBufferWriter<byte>
    {
        private int _written;

        public void Advance(int count) => _written += count;

        public Memory<byte> GetMemory(int sizeHint = 0) => buffer.AsMemory(_written);

        public Span<byte> GetSpan(int sizeHint = 0) => buffer.AsSpan(_written);
    }
}
