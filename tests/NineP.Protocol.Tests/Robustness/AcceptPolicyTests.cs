using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NineP.Protocol.Internal;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using NineP.Protocol.Transports;
using NineP.Protocol.Transports.Internal;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests.Robustness;

/// <summary>
/// What an accept loop does with a socket error. Returning null is the <c>INinePListener</c>
/// contract's "this listener is finished" signal, so answering every <see cref="SocketException"/>
/// with it stopped the whole service for one transient refusal - an ECONNABORTED from a peer that
/// gave up between the SYN and the accept, an EMFILE while the process was briefly out of
/// descriptors - while the process stayed up and looked healthy.
/// <b>Mutation:</b> make <c>AcceptPolicy.IsFatal</c> return true unconditionally, which is what
/// the two listeners used to do, and
/// <see cref="ATransientFailureIsRetriedAndTheListenerLivesOn"/> dies.
/// </summary>
[Trait("Category", "Robustness")]
public sealed class AcceptPolicyTests
{
    private static readonly NinePAddress Address = new(NinePScheme.Tcp, "127.0.0.1", 1564, string.Empty);

    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>A transient refusal is logged, waited out and tried again.</summary>
    [Fact]
    public async Task ATransientFailureIsRetriedAndTheListenerLivesOn()
    {
        RecordingLogger logger = new();
        int attempts = 0;
        object marker = new();

        object? accepted = await AcceptPolicy.AcceptAsync(
            _ =>
            {
                attempts++;
                return attempts <= 3
                    ? throw new SocketException((int)SocketError.ConnectionAborted)
                    : ValueTask.FromResult(marker);
            },
            logger,
            Address,
            () => false,
            Ct);

        Assert.Same(marker, accepted);
        Assert.Equal(4, attempts);
        Assert.Equal(3, logger.Warnings.Count);
        Assert.All(logger.Warnings, line => Assert.Contains("retrying in", line, StringComparison.Ordinal));

        // The backoff grows rather than hammering the socket.
        Assert.Contains("5 ms", logger.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("10 ms", logger.Warnings[1], StringComparison.Ordinal);
        Assert.Contains("20 ms", logger.Warnings[2], StringComparison.Ordinal);
    }

    /// <summary>An error that means the listening socket is gone ends the loop, and says nothing.</summary>
    /// <param name="error">The socket error the accept raises.</param>
    /// <returns>A task that completes when the loop has ended.</returns>
    [Theory]
    [InlineData(SocketError.OperationAborted)]
    [InlineData(SocketError.Interrupted)]
    [InlineData(SocketError.Shutdown)]
    [InlineData(SocketError.NotSocket)]
    [InlineData(SocketError.InvalidArgument)]
    public async Task AFatalFailureEndsTheLoop(SocketError error)
    {
        RecordingLogger logger = new();
        int attempts = 0;

        object? accepted = await AcceptPolicy.AcceptAsync<object>(
            _ =>
            {
                attempts++;
                throw new SocketException((int)error);
            },
            logger,
            Address,
            () => false,
            Ct);

        Assert.Null(accepted);
        Assert.Equal(1, attempts);
        Assert.Empty(logger.Warnings);
    }

    /// <summary>Disposal ends the loop whichever way it surfaces: as the exception, or as the flag.</summary>
    [Fact]
    public async Task DisposalEndsTheLoop()
    {
        RecordingLogger logger = new();

        Assert.Null(await AcceptPolicy.AcceptAsync<object>(
            _ => throw new ObjectDisposedException("listener"), logger, Address, () => false, Ct));

        // A transient error raised after the listener was closed is the close, not a refusal.
        Assert.Null(await AcceptPolicy.AcceptAsync<object>(
            _ => throw new SocketException((int)SocketError.ConnectionAborted),
            logger,
            Address,
            () => true,
            Ct));

        Assert.Empty(logger.Warnings);
    }

    /// <summary>A cancelled listener stops waiting rather than retrying for ever.</summary>
    [Fact]
    public async Task CancellationEndsTheLoop()
    {
        using CancellationTokenSource stopping = new();
        RecordingLogger logger = new();
        int attempts = 0;

        // Cancelled before the first attempt runs, so the backoff's wait is what observes it.
        await stopping.CancelAsync();

        object? accepted = await AcceptPolicy.AcceptAsync<object>(
            _ =>
            {
                attempts++;
                throw new SocketException((int)SocketError.ConnectionAborted);
            },
            logger,
            Address,
            () => false,
            stopping.Token);

        Assert.Null(accepted);
        Assert.Equal(1, attempts);
    }
}
