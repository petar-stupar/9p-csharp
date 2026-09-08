using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Internal;
using NineP.Protocol.Messages;
using NineP.Protocol.Negotiation;
using NineP.Protocol.Transports;

namespace NineP.Client.Internal;

/// <summary>
/// The client's reply router (architecture §6). One reader task fills a pipe, one loop takes whole
/// frames off it, and every reply is matched to its tag. Reference §8 rule 12 is enforced here and
/// nowhere else: a reply with an unknown tag, an unexpected type, or a size over the negotiated
/// msize is a protocol error that <b>terminates the connection</b> rather than being skipped —
/// there is no way to tell how much of the stream is still trustworthy.
/// </summary>
internal sealed class TagMultiplexer : IAsyncDisposable
{
    private readonly INinePConnection _connection;
    private readonly Limits _limits;
    private readonly ILogger _logger;
    private readonly TagPool _tags = new();
    private readonly ConcurrentDictionary<ushort, PendingRequest> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Pipe _inbound = new();
    private readonly FrameReader _frames;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TimeSpan _requestTimeout;

    private TaskCompletionSource<byte[]>? _versionWaiter;
    private Task _filling = Task.CompletedTask;
    private Task _reading = Task.CompletedTask;
    private Exception? _terminated;
    private Dialect _dialect;
    private uint _msize;
    private int _disposed;

    /// <summary>Creates a multiplexer over a connected transport.</summary>
    /// <param name="connection">The connection; the multiplexer owns its reads and writes.</param>
    /// <param name="limits">The bounds this side enforces on incoming frames.</param>
    /// <param name="logger">Where terminations are logged.</param>
    /// <param name="requestTimeout">How long one request may take before it is flushed; Zero disables it.</param>
    public TagMultiplexer(
        INinePConnection connection, Limits limits, ILogger logger, TimeSpan requestTimeout)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _limits = limits;
        _logger = logger;
        _requestTimeout = requestTimeout;
        _frames = new FrameReader(_inbound.Reader, limits, ArrayPool<byte>.Shared);
    }

    /// <summary>The dialect the session negotiated; 9P2000 until <see cref="SetNegotiated"/> runs.</summary>
    public Dialect Dialect => _dialect;

    /// <summary>The msize the two sides agreed on; zero before negotiation.</summary>
    public uint Msize => _msize;

    /// <summary>Requests written and not yet answered.</summary>
    public int Outstanding => _pending.Count;

    /// <summary>Tags rented from the pool, which a flushed request still holds.</summary>
    public int TagsInUse => _tags.Outstanding;

    /// <summary>Why the session terminated, or null while it is healthy.</summary>
    public Exception? Termination => Volatile.Read(ref _terminated);

    /// <summary>Starts the reader tasks. Called once, before the first request.</summary>
    public void Start()
    {
        _filling = Task.Run(FillAsync, CancellationToken.None);
        _reading = Task.Run(RouteAsync, CancellationToken.None);
    }

    /// <summary>Takes the negotiated dialect and msize as the bounds from the next frame onward.</summary>
    /// <param name="dialect">The dialect that was agreed.</param>
    /// <param name="msize">The msize that was agreed.</param>
    public void SetNegotiated(Dialect dialect, uint msize)
    {
        _dialect = dialect;
        _msize = msize;
        _frames.SetNegotiatedMsize(msize);
    }

    /// <summary>
    /// Sends the <c>Tversion</c> that opens a session. It carries <c>NOTAG</c> and takes no tag
    /// from the pool, so it cannot collide with a request (reference §5.1).
    /// </summary>
    /// <param name="request">The version proposal.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The server's answer.</returns>
    /// <exception cref="NinePProtocolException">The session terminated before an answer arrived.</exception>
    public async ValueTask<Rversion> VersionAsync(Tversion request, CancellationToken cancellationToken)
    {
        ThrowIfTerminated();

        TaskCompletionSource<byte[]> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _versionWaiter, waiter);

        await WriteAsync(request with { Tag = Constants.NOTAG }, cancellationToken).ConfigureAwait(false);

        byte[] frame = await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return MessageCodec.Decode<Rversion>(frame, Dialect.P9_2000);
    }

    /// <summary>Sends one request under a tag of its own and returns the typed reply.</summary>
    /// <typeparam name="TRequest">The T-message record.</typeparam>
    /// <typeparam name="TResponse">The R-message record the server must answer with.</typeparam>
    /// <param name="build">Builds the request once the multiplexer has chosen its tag.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    /// <exception cref="NinePProtocolException">The session terminated, or the tag pool is empty.</exception>
    public ValueTask<TResponse> RequestAsync<TRequest, TResponse>(
        Func<ushort, TRequest> build, CancellationToken cancellationToken)
        where TRequest : struct, IMessage
        where TResponse : struct, IMessage =>
        SendAsync<TRequest, TResponse>(build, flushable: true, cancellationToken);

    /// <summary>
    /// Sends a <c>Tflush</c> the caller asked for. A <c>Tflush</c> is never itself flushed: it is
    /// what cancellation turns into, and flushing it would only need another one.
    /// </summary>
    /// <param name="request">The flush; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the wait for the <c>Rflush</c>.</param>
    /// <returns>The <c>Rflush</c>, which the server sends in every case, even for an unknown tag.</returns>
    public ValueTask<Rflush> FlushRequestAsync(Tflush request, CancellationToken cancellationToken) =>
        SendAsync<Tflush, Rflush>(tag => request with { Tag = tag }, flushable: false, cancellationToken);

    private async ValueTask<TResponse> SendAsync<TRequest, TResponse>(
        Func<ushort, TRequest> build,
        bool flushable,
        CancellationToken cancellationToken,
        (ushort Tag, PendingRequest Request)? flushed = null)
        where TRequest : struct, IMessage
        where TResponse : struct, IMessage
    {
        ArgumentNullException.ThrowIfNull(build);
        ThrowIfTerminated();
        cancellationToken.ThrowIfCancellationRequested();

        if (!DialectLegality.IsLegal(TRequest.Type, _dialect))
        {
            throw new NinePProtocolException(
                ProtocolErrorKind.Type,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} is not legal in a {1} session",
                    MessageTypes.GetName(TRequest.Type),
                    Negotiator.VersionString(_dialect)));
        }

        if (!_tags.TryRent(out ushort tag))
        {
            throw new NinePProtocolException(
                ProtocolErrorKind.Overflow, "every one of the 65535 tags is outstanding");
        }

        PendingRequest pending = new(TResponse.Type) { Flushed = flushed };
        _pending[tag] = pending;

        try
        {
            // The write itself is not cancellable: a frame goes out whole, and a half-written one
            // would desynchronise the connection for every other request on it.
            await WriteAsync(build(tag), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            Release(tag, pending);
            throw;
        }

        if (!flushable)
        {
            byte[] answer;

            try
            {
                answer = await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Termination is null)
            {
                // The only non-flushable request is a Tflush, and a Tflush is never itself
                // flushed, so nothing will ever ask this server again about this tag. It is
                // quarantined rather than returned: the server never said it had finished with
                // it, and a late Rflush for a number that had gone back to the pool would meet
                // DeliverAsync's unknown-tag rule and terminate the whole session over a merely
                // slow peer -- or, worse, be delivered to whichever request rented the number
                // next. The entry stays in _pending so a late Rflush is recognised and dropped,
                // and the number stays out of circulation until the session ends -- or until a
                // late Rflush does arrive, which frees both (see DeliverAsync).
                Observe(pending);
                throw;
            }
            catch
            {
                Release(tag, pending);
                throw;
            }

            try
            {
                return Decode<TResponse>(answer);
            }
            finally
            {
                Release(tag, pending);
            }
        }

        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_requestTimeout > TimeSpan.Zero)
        {
            deadline.CancelAfter(_requestTimeout);
        }

        byte[] frame;
        try
        {
            frame = await pending.Completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Termination is null)
        {
            // The tag stays rented: FlushAsync owns it until the Rflush confirms the server is
            // finished with it, and it is the only path that releases it.
            return await FlushAsync<TResponse>(tag, pending, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release(tag, pending);
            throw;
        }

        try
        {
            // Decode throws for every Rerror/Rlerror, which is an ordinary reply and not a reason
            // to release the tag twice; the release therefore happens once, here, either way.
            return Decode<TResponse>(frame);
        }
        finally
        {
            Release(tag, pending);
        }
    }

    /// <summary>
    /// The client half of flush(5) and reference §8 rule 14. The tag is <b>not</b> returned to the
    /// pool when the request is abandoned — only once the <c>Rflush</c> has arrived, because until
    /// then the server may still answer it, and a reused tag would collide with that answer. A
    /// reply that arrived before the <c>Rflush</c> is delivered to the caller normally: a
    /// <c>Tcreate</c> may have created and a <c>Twalk</c> may have bound a fid, and pretending
    /// otherwise would leak both.
    /// <para>
    /// The wait for that <c>Rflush</c> carries the session's <see cref="ClientOptions.RequestTimeout"/>.
    /// Waiting with no deadline at all hangs the caller against a server that is connected but
    /// silent — and with it <c>NinePSession.DisposeAsync</c>, which clunks every fid before it
    /// closes the socket. The caller's own token is deliberately not observed here: it is usually
    /// already cancelled, since it is what asked for this flush, and abandoning the <c>Tflush</c>
    /// would free a tag the server has not said it is finished with.
    /// </para>
    /// </summary>
    private async ValueTask<TResponse> FlushAsync<TResponse>(
        ushort oldTag, PendingRequest pending, CancellationToken cancellationToken)
        where TResponse : struct, IMessage
    {
        using CancellationTokenSource deadline = new();
        if (_requestTimeout > TimeSpan.Zero)
        {
            deadline.CancelAfter(_requestTimeout);
        }

        try
        {
            await SendAsync<Tflush, Rflush>(
                tag => new Tflush(tag, oldTag), flushable: false, deadline.Token, (oldTag, pending))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // No Rflush inside the deadline. Both tags stay rented on purpose -- the flushed
            // request's, and the Tflush's own, which SendAsync quarantines on the way out. The
            // server never said it had finished with either, so handing a number to another
            // request would let a late reply be delivered to the wrong caller, and a late Rflush
            // for a number back in the pool would terminate the session as an unknown tag.
            // Nothing awaits the flushed request's task from here on, so it is observed: the
            // session's termination faults every entry in _pending, and an unread fault is an
            // unobserved task exception.
            Observe(pending);

            throw new TimeoutException(string.Format(
                CultureInfo.InvariantCulture,
                "the Tflush that cancelled the request was not answered within {0}", _requestTimeout));
        }
        catch (NinePException refused) when (refused is not NinePProtocolException)
        {
            // The server answered the Tflush with Rerror/Rlerror. flush(5) knows no such reply,
            // but it is an answer to the Tflush all the same, and DeliverAsync has released both
            // tags on it as it would on an Rflush (the Flushed branch there). The caller gets what
            // an Rflush would have given it: the reply that raced ahead, if one did, and otherwise
            // its own cancellation or the timeout. A peer that does this is worth knowing about,
            // so the refusal is logged. A NinePProtocolException is not this case -- it is the
            // session terminating, or the tag pool running dry -- and it propagates.
            _logger.FlushAnsweredWithError(refused.Error.Errno, UntrustedText.Sanitize(refused.Error.Ename));
        }

        // The Rflush is the server's word that the tag is free. DeliverAsync has usually done
        // this already, from the Flushed pair the Tflush's entry carries; this is the backstop for
        // an Rflush that raced its way here first, and it removes by identity either way.
        Release(oldTag, pending);

        if (pending.Completion.Task.IsCompletedSuccessfully)
        {
            // The task has already completed, so this await does not wait; it is how the
            // result is read without blocking a thread on Task.Result.
            return Decode<TResponse>(await pending.Completion.Task.ConfigureAwait(false));
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException(string.Format(
            CultureInfo.InvariantCulture,
            "the request took longer than {0} and was flushed", _requestTimeout));
    }

    /// <summary>
    /// Watches a quarantined request's task so that whatever settles it later -- a late reply, or
    /// the session's termination faulting every pending entry -- is observed. Nothing awaits it
    /// any more; without this its exception would be an unobserved one.
    /// </summary>
    /// <param name="pending">The request whose tag has been quarantined.</param>
    private static void Observe(PendingRequest pending) =>
        _ = pending.Completion.Task.ContinueWith(
            static settled => _ = settled.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Gives a tag back exactly once. The pending request is removed <b>by identity</b>, not by
    /// number, and the tag goes back to the pool only when that removal is the one that took it:
    /// a second call for the same request finds the slot already gone — or holding whichever
    /// request rented the number next — and does nothing. Releasing by number instead would
    /// double-return the tag, and two live requests would then share it and cross-deliver their
    /// replies. The server's <c>TagTable.Release</c> takes the same shape for the same reason.
    /// </summary>
    /// <param name="tag">The tag the request was written under.</param>
    /// <param name="pending">The request that holds it.</param>
    private void Release(ushort tag, PendingRequest pending)
    {
        if (_pending.TryRemove(new KeyValuePair<ushort, PendingRequest>(tag, pending)))
        {
            _tags.Return(tag);
        }
    }

    /// <summary>Terminates the session and closes the connection.</summary>
    /// <returns>A task that completes when the reader tasks have stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await TerminateAsync(
            new NinePProtocolException(ProtocolErrorKind.Size, "the session was disposed"),
            CloseReason.Normal).ConfigureAwait(false);

        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(_filling, _reading).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is OperationCanceledException or IOException or NinePException)
        {
            // The reader tasks are being torn down; how they ended is not news.
        }

        _shutdown.Dispose();
        _writeGate.Dispose();
    }

    /// <summary>Writes one complete frame, serialised through the single writer.</summary>
    /// <typeparam name="TMessage">The record being written.</typeparam>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the frame has been handed to the transport.</returns>
    internal async ValueTask WriteAsync<TMessage>(TMessage message, CancellationToken cancellationToken)
        where TMessage : struct, IMessage
    {
        RequestNameLimit.Validate(in message, _limits.MaxNameLength);

        // Tversion negotiates the dialect, so it is encoded in the base dialect every peer decodes.
        Dialect dialect = TMessage.Type == MessageType.Tversion ? Dialect.P9_2000 : _dialect;
        ArrayBufferWriter<byte> writer = new(MessageCodec.GetEncodedSize(in message, dialect));
        MessageCodec.Encode(writer, in message, dialect);

        // One writer per connection: a 9P frame is written whole or not at all, and two writers
        // would be able to interleave halves of two frames on the wire.
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _connection.WriteAsync(writer.WrittenMemory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private TResponse Decode<TResponse>(byte[] frame)
        where TResponse : struct, IMessage
    {
        MessageType type = MessageCodec.PeekType(frame);
        if (type != TResponse.Type)
        {
            throw ToException(frame, type);
        }

        return MessageCodec.Decode<TResponse>(frame, _dialect);
    }

    private NinePException ToException(byte[] frame, MessageType type) => type switch
    {
        MessageType.Rlerror => new NinePException(
            ErrorProjector.FromRlerror(MessageCodec.Decode<Rlerror>(frame, _dialect))),
        MessageType.Rerror => new NinePException(
            ErrorProjector.FromRerror(MessageCodec.Decode<Rerror>(frame, _dialect))),
        _ => new NinePProtocolException(
            ProtocolErrorKind.Type,
            string.Format(CultureInfo.InvariantCulture, "unexpected reply type {0}", (byte)type)),
    };

    private async Task FillAsync()
    {
        try
        {
            while (true)
            {
                Memory<byte> destination = _inbound.Writer.GetMemory(4096);
                int read = await _connection.ReadAsync(destination, _shutdown.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                _inbound.Writer.Advance(read);
                FlushResult flush = await _inbound.Writer.FlushAsync(_shutdown.Token).ConfigureAwait(false);
                if (flush.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception failure) when (failure is OperationCanceledException or IOException
            or ObjectDisposedException or NinePProtocolException)
        {
            // The connection went away; the routing loop turns that into a termination.
        }
        finally
        {
            await _inbound.Writer.CompleteAsync().ConfigureAwait(false);
        }
    }

    private async Task RouteAsync()
    {
        try
        {
            while (true)
            {
                FrameReadResult result = await _frames.ReadFrameAsync(_shutdown.Token).ConfigureAwait(false);

                if (result.Failure is ProtocolErrorKind failure)
                {
                    await TerminateAsync(
                        new NinePProtocolException(failure, "the server sent an illegal frame"),
                        result.Close ?? CloseReason.ProtocolViolation).ConfigureAwait(false);
                    return;
                }

                if (result.IsEndOfStream || !result.HasFrame)
                {
                    await TerminateAsync(
                        new NinePProtocolException(ProtocolErrorKind.Size, "the server closed the connection"),
                        CloseReason.PeerClosed).ConfigureAwait(false);
                    return;
                }

                using FrameLease lease = result.Lease!;
                if (!await DeliverAsync(lease.Frame).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose asked the loop to stop; the pending requests were already faulted.
        }
    }

    private async ValueTask<bool> DeliverAsync(ReadOnlyMemory<byte> frame)
    {
        MessageType type = MessageCodec.PeekType(frame.Span);
        ushort tag = MessageCodec.PeekTag(frame.Span);

        if (type == MessageType.Rversion && tag == Constants.NOTAG)
        {
            // The waiter is taken, not read: an Rversion answers one Tversion, and the next frame
            // carrying NOTAG has no exchange left to belong to. Reference §8 rule 18 sends such a
            // frame to rule 12 as an unexpected reply, and rule 12 terminates -- discarding it, as
            // this did, left the session running on a stream whose framing nobody could vouch for
            // and whose sender had just claimed to renegotiate it.
            if (Interlocked.Exchange(ref _versionWaiter, null) is { } waiter)
            {
                waiter.TrySetResult(frame.ToArray());
                return true;
            }

            await TerminateAsync(
                new NinePProtocolException(
                    ProtocolErrorKind.Type, "the server sent an Rversion with no Tversion outstanding"),
                CloseReason.ProtocolViolation).ConfigureAwait(false);
            return false;
        }

        if (!_pending.TryGetValue(tag, out PendingRequest? pending))
        {
            await TerminateAsync(
                new NinePProtocolException(
                    ProtocolErrorKind.Type,
                    string.Format(CultureInfo.InvariantCulture, "the server answered unknown tag {0}", tag)),
                CloseReason.ProtocolViolation).ConfigureAwait(false);
            return false;
        }

        if (type != pending.Expected && type != ErrorProjector.ErrorTypeFor(_dialect))
        {
            await TerminateAsync(
                new NinePProtocolException(
                    ProtocolErrorKind.Type,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "the server answered tag {0} with type {1}, not {2}",
                        tag,
                        (byte)type,
                        (byte)pending.Expected)),
                CloseReason.ProtocolViolation).ConfigureAwait(false);
            return false;
        }

        if (pending.Flushed is { } cancelled)
        {
            // Reference §5.3: the Rflush says the server is finished with oldtag as well, so both
            // numbers go back to the pool -- including the pair a timed-out FlushAsync had
            // quarantined, which used to stay rented for the life of the session although the
            // server had by then confirmed them both.
            //
            // The type check above has let through only an Rflush or the dialect's error type.
            // A server that answers a Tflush with Rerror/Rlerror is not conformant -- flush(5)
            // has no such reply -- but it has answered the Tflush, and §5.3's ordering rule, that
            // no reply to oldtag follows the answer to the Tflush, is the server's to keep
            // whichever type it chose. So an error releases both tags exactly as an Rflush does.
            // Releasing only the Tflush's own tag, as the ordinary error path below would, left
            // oldtag rented for the life of the session on the word of a peer that had, by its
            // own reply, finished with it. FlushAsync logs the refusal.
            pending.Completion.TrySetResult(frame.ToArray());
            Release(cancelled.Tag, cancelled.Request);
            Release(tag, pending);
            return true;
        }

        pending.Completion.TrySetResult(frame.ToArray());
        return true;
    }

    private async ValueTask TerminateAsync(Exception failure, CloseReason reason)
    {
        if (Interlocked.CompareExchange(ref _terminated, failure, null) is not null)
        {
            return;
        }

        _logger.SessionTerminated(failure.Message);

        Interlocked.Exchange(ref _versionWaiter, null)?.TrySetException(failure);
        foreach (KeyValuePair<ushort, PendingRequest> entry in _pending)
        {
            entry.Value.Completion.TrySetException(failure);
        }

        await _connection.CloseAsync(reason, CancellationToken.None).ConfigureAwait(false);
    }

    private void ThrowIfTerminated()
    {
        if (Termination is { } failure)
        {
            throw new NinePProtocolException("the 9P session has terminated", failure);
        }
    }
}
