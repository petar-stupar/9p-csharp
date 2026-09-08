using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Transports;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>
/// Reference §8 rule 1: the size field is validated against the active bound before the rest of
/// the frame is waited for, and a violation closes the connection because a stream that lied about
/// a length cannot be resynced. The bound is the constant 8192 until Rversion and the negotiated
/// msize afterwards — never the configured maximum, which an unauthenticated peer could otherwise
/// make every connection reserve.
/// </summary>
public sealed class FrameReaderTests
{
    private const uint PreNegotiationCap = 8192;

    private static CancellationToken Token => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>A frame that claims more than the bound is a size violation and a close.</summary>
    [Fact]
    public async Task SizeLieClosesConnection()
    {
        Pipe pipe = new();
        FrameReader reader = NewReader(pipe);
        reader.SetNegotiatedMsize(PreNegotiationCap);

        await Write(pipe, Claiming(PreNegotiationCap + 1));
        FrameReadResult result = await reader.ReadFrameAsync(Token);

        Assert.False(result.HasFrame);
        Assert.Equal(ProtocolErrorKind.Size, result.Failure);
        Assert.Equal(CloseReason.MessageTooLarge, result.Close);
    }

    /// <summary>
    /// Before negotiation the bound is the constant 8192 and not <see cref="Limits.MaxMsize"/>:
    /// swap the two and this test dies, which is the point of it.
    /// </summary>
    [Fact]
    public async Task PreNegotiationCapIs8192()
    {
        Pipe pipe = new();
        FrameReader reader = NewReader(pipe);

        Assert.Equal(PreNegotiationCap, reader.ActiveBound);
        Assert.False(reader.IsNegotiated);
        Assert.True(Limits.Default.MaxMsize > PreNegotiationCap);

        // A frame that the configured maximum would allow, and the cap does not.
        await Write(pipe, Claiming(PreNegotiationCap + 1));
        FrameReadResult refused = await reader.ReadFrameAsync(Token);

        Assert.Equal(CloseReason.MessageTooLarge, refused.Close);
        Assert.Equal(ProtocolErrorKind.Size, refused.Failure);
    }

    /// <summary>Once an msize is agreed it becomes the bound, and a reset puts the cap back.</summary>
    [Fact]
    public void TheNegotiatedMsizeBecomesTheBound()
    {
        Pipe pipe = new();
        FrameReader reader = NewReader(pipe);

        reader.SetNegotiatedMsize(64 * 1024);
        Assert.Equal(64u * 1024, reader.ActiveBound);
        Assert.True(reader.IsNegotiated);

        reader.ResetToPreNegotiation();
        Assert.Equal(PreNegotiationCap, reader.ActiveBound);
        Assert.False(reader.IsNegotiated);
    }

    /// <summary>A frame delivered one byte at a time is reassembled and decodes.</summary>
    [Fact]
    public async Task ReassemblesSplitFrame()
    {
        WireVector vector = WireVectors.All.First(v => v.Size > Constants.HDRSZ);
        byte[] frame = vector.ToBytes();

        Pipe pipe = new();
        FrameReader reader = NewReader(pipe);
        Task<FrameReadResult> reading = reader.ReadFrameAsync(Token).AsTask();

        foreach (byte b in frame)
        {
            await pipe.Writer.WriteAsync(new[] { b }, Token);
        }

        FrameReadResult result = await reading;

        Assert.True(result.HasFrame);
        using FrameLease lease = result.Lease!;
        Assert.Equal(frame, lease.Frame.ToArray());
    }

    /// <summary>A frame spread over several pipe segments is copied once into a pooled rental.</summary>
    [Fact]
    public async Task AMultiSegmentFrameIsCopiedIntoThePool()
    {
        WireVector vector = WireVectors.All.First(v => v.Size > 32);
        byte[] frame = vector.ToBytes();

        // Tiny segments force the pipe to hand the reader a multi-segment sequence.
        Pipe pipe = new(new PipeOptions(minimumSegmentSize: 16, useSynchronizationContext: false));
        FrameReader reader = NewReader(pipe);

        for (int at = 0; at < frame.Length; at += 8)
        {
            await pipe.Writer.WriteAsync(frame.AsMemory(at, Math.Min(8, frame.Length - at)), Token);
        }

        FrameReadResult result = await reader.ReadFrameAsync(Token);

        Assert.True(result.HasFrame);
        using FrameLease lease = result.Lease!;
        Assert.True(lease.IsPooled);
        Assert.Equal(frame, lease.Frame.ToArray());
    }

    /// <summary>A size below the seven-byte header is a framing violation.</summary>
    [Fact]
    public async Task RejectsSizeBelow7()
    {
        Pipe pipe = new();
        FrameReader reader = NewReader(pipe);

        await Write(pipe, Claiming(Constants.HDRSZ - 1));
        FrameReadResult result = await reader.ReadFrameAsync(Token);

        Assert.False(result.HasFrame);
        Assert.Equal(ProtocolErrorKind.Size, result.Failure);
        Assert.Equal(CloseReason.ProtocolViolation, result.Close);
    }

    /// <summary>A peer that closes between frames is not an error; one that closes inside one is.</summary>
    [Fact]
    public async Task EndOfStreamIsCleanBetweenFramesAndAViolationInsideOne()
    {
        Pipe clean = new();
        FrameReader cleanReader = NewReader(clean);
        await clean.Writer.CompleteAsync();

        FrameReadResult closed = await cleanReader.ReadFrameAsync(Token);
        Assert.True(closed.IsEndOfStream);
        Assert.Null(closed.Failure);
        Assert.Equal(CloseReason.PeerClosed, closed.Close);

        Pipe truncated = new();
        FrameReader truncatedReader = NewReader(truncated);
        await truncated.Writer.WriteAsync(Claiming(64).AsMemory(0, 5), Token);
        await truncated.Writer.CompleteAsync();

        FrameReadResult cut = await truncatedReader.ReadFrameAsync(Token);
        Assert.True(cut.IsEndOfStream);
        Assert.Equal(ProtocolErrorKind.Size, cut.Failure);
        Assert.Equal(CloseReason.ProtocolViolation, cut.Close);
    }

    /// <summary>Disposing a lease advances the pipe, so the next frame is the next frame.</summary>
    [Fact]
    public async Task DisposingALeaseAdvancesThePipe()
    {
        byte[] first = WireVectors.All[0].ToBytes();
        byte[] second = WireVectors.All[1].ToBytes();

        Pipe pipe = new();
        FrameReader reader = NewReader(pipe);
        await Write(pipe, [.. first, .. second]);

        FrameReadResult one = await reader.ReadFrameAsync(Token);
        Assert.Equal(first, one.Lease!.Frame.ToArray());
        one.Lease.Dispose();

        FrameReadResult two = await reader.ReadFrameAsync(Token);
        Assert.Equal(second, two.Lease!.Frame.ToArray());
        two.Lease.Dispose();
    }

    private static FrameReader NewReader(Pipe pipe) =>
        new(pipe.Reader, Limits.Default, ArrayPool<byte>.Create(64 * 1024, 4));

    private static async Task Write(Pipe pipe, byte[] bytes)
    {
        await pipe.Writer.WriteAsync(bytes, Token);

        // Completing the writer is what makes the size checks observable rather than a hang: with
        // the bound wrongly set to the configured maximum the reader would wait for the bytes the
        // frame claims, and this test would time out instead of failing.
        await pipe.Writer.CompleteAsync();
    }

    private static byte[] Claiming(long size)
    {
        byte[] frame = new byte[Math.Min(size < 0 ? 8 : size + 8, 64)];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)size);
        return frame;
    }
}
