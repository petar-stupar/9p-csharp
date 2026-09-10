using System.Buffers;
using System.Text;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using Xunit;

namespace NineP.Protocol.Tests.Conformance;

/// <summary>One error value, three wire shapes, chosen by the session dialect (§6.3).</summary>
[Trait("Category", "Conformance")]
public sealed class ErrorProjectionTests
{
    /// <summary>Rule 34: a .L session never carries an Rerror; every error is an Rlerror errno.</summary>
    [Fact]
    public void DotLAlwaysUsesRlerror()
    {
        byte[] frame = Project(new NinePError("file not found", Errno.ENOENT), Dialect.P9_2000_L);

        Assert.Equal(MessageType.Rlerror, MessageCodec.PeekType(frame));
        Assert.Equal(11u, MessageCodec.PeekSize(frame));
        Assert.Equal(
            new byte[] { 0x0B, 0, 0, 0, (byte)MessageType.Rlerror, 0x07, 0x00, 0x02, 0, 0, 0 }, frame);
    }

    /// <summary>9P2000 carries the ename alone; .u appends the errno to the very same text.</summary>
    [Fact]
    public void LegacyDialectsCarryTheEname()
    {
        NinePError error = new("file not found", Errno.ENOENT);
        byte[] ename = Encoding.ASCII.GetBytes("file not found");

        byte[] plain = Project(error, Dialect.P9_2000);
        byte[] unix = Project(error, Dialect.P9_2000_u);

        Assert.Equal(
            [.. new byte[] { 0x17, 0, 0, 0, (byte)MessageType.Rerror, 0x07, 0x00, 0x0E, 0x00 }, .. ename],
            plain);
        Assert.Equal(
            [.. new byte[] { 0x1B, 0, 0, 0, (byte)MessageType.Rerror, 0x07, 0x00, 0x0E, 0x00 }, .. ename, .. new byte[] { 0x02, 0, 0, 0 }],
            unix);
    }

    /// <summary>The error type number a dialect carries is fixed by the dialect, not by the value.</summary>
    [Theory]
    [InlineData(Dialect.P9_2000, MessageType.Rerror)]
    [InlineData(Dialect.P9_2000_u, MessageType.Rerror)]
    [InlineData(Dialect.P9_2000_L, MessageType.Rlerror)]
    public void ErrorTypeFollowsTheDialect(Dialect dialect, MessageType expected) =>
        Assert.Equal(expected, ErrorProjector.ErrorTypeFor(dialect));

    /// <summary>Every row of the table survives the round trip through all three shapes.</summary>
    [Fact]
    public void EveryTableRowRoundTripsThroughEveryDialect()
    {
        foreach (NinePError error in ErrorTable.All)
        {
            Rerror plain = ErrorProjector.ToRerror(9, error, Dialect.P9_2000);
            Rerror unix = ErrorProjector.ToRerror(9, error, Dialect.P9_2000_u);
            Rlerror linux = ErrorProjector.ToRlerror(9, error);

            Assert.Equal(0, plain.Errno);
            Assert.Equal(error.Errno, unix.Errno);
            Assert.Equal(error.Errno, linux.Ecode);
            Assert.Equal(error.Ename, ErrorProjector.FromRerror(in unix).Ename);
            Assert.Equal(error.Errno, ErrorProjector.FromRlerror(in linux).Errno);
        }
    }

    /// <summary>A .u reply's own errno wins over the table, so a peer's mapping is not overwritten.</summary>
    [Fact]
    public void UnixReplyErrnoWinsOverTheTable()
    {
        Rerror reply = new(9, "file not found", Errno.EACCES);

        Assert.Equal(Errno.EACCES, ErrorProjector.FromRerror(in reply).Errno);
    }

    /// <summary>A 9P2000 reply has no errno (0), so the ename is mapped back through the table.</summary>
    [Fact]
    public void LegacyReplyErrnoComesFromTheTable()
    {
        Rerror reply = new(9, "is a directory", 0);

        Assert.Equal(Errno.EISDIR, ErrorProjector.FromRerror(in reply).Errno);
    }

    /// <summary>
    /// A negative errno is not an errno: the encoder refuses to put one on the wire, so the
    /// projector never hands it one. It is a bug on this side, and like every other bug on this
    /// side the peer is told "i/o error" -- in the errno field alone, so the ename the handler
    /// chose still reaches a 9P2000 or .u peer.
    /// </summary>
    [Fact]
    public void ANegativeErrnoIsProjectedAsEio()
    {
        NinePError error = new("something went wrong", -2);

        Assert.Equal(0, ErrorProjector.ToRerror(9, error, Dialect.P9_2000).Errno);
        Assert.Equal(Errno.EIO, ErrorProjector.ToRerror(9, error, Dialect.P9_2000_u).Errno);
        Assert.Equal(Errno.EIO, ErrorProjector.ToRlerror(9, error).Ecode);
        Assert.Equal("something went wrong", ErrorProjector.ToRerror(9, error, Dialect.P9_2000_u).Ename);
    }

    /// <summary>
    /// A failure that is not a NinePException is a bug on this side: the peer is told "i/o error"
    /// and never the exception's message, which could carry a path (reference §8 rule 10).
    /// </summary>
    [Fact]
    public void NonNinePFailuresProjectToEio()
    {
        NinePError projected = ErrorProjector.FromException(
            new InvalidOperationException("/var/lib/secret/state.db is corrupt"));

        Assert.Equal("i/o error", projected.Ename);
        Assert.Equal(Errno.EIO, projected.Errno);
    }

    /// <summary>A NinePException projects to exactly the value it was raised with.</summary>
    [Fact]
    public void NinePFailuresKeepTheirValue()
    {
        NinePError projected = ErrorProjector.FromException(
            new NinePException(NinePError.FromErrno(Errno.EACCES)));

        Assert.Equal(new NinePError("permission denied", Errno.EACCES), projected);
    }

    /// <summary>An over-long ename is truncated before it reaches the wire, in every dialect.</summary>
    [Theory]
    [InlineData(Dialect.P9_2000)]
    [InlineData(Dialect.P9_2000_u)]
    public void ProjectedEnameIsTruncated(Dialect dialect)
    {
        NinePError error = new(new string('x', 400), Errno.EIO);

        Rerror reply = ErrorProjector.ToRerror(9, error, dialect);

        Assert.Equal(Constants.ERRMAX - 1, reply.Ename.Length);
    }

    private static byte[] Project(NinePError error, Dialect dialect)
    {
        ArrayBufferWriter<byte> writer = new();
        int written = ErrorProjector.Write(writer, 7, error, dialect);

        Assert.Equal(written, writer.WrittenCount);
        return writer.WrittenSpan.ToArray();
    }
}
