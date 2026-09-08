using NineP.Protocol;
using NineP.Protocol.Messages;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>The stat record of reference §4.2 and its "don't touch" convention.</summary>
public sealed class StatRecordTests
{
    /// <summary>
    /// A Twstat whose every integer field is ~0 of its width and whose every string is empty is a
    /// request to commit the file to stable storage, not a request to change anything.
    /// </summary>
    [Fact]
    public void AllDontTouchIsFsyncRequest()
    {
        Assert.True(StatRecord.DontTouch.IsAllDontTouch);
        Assert.False(default(StatRecord).IsAllDontTouch);
        Assert.False((StatRecord.DontTouch with { Mode = 0x1A4 }).IsAllDontTouch);
        Assert.False((StatRecord.DontTouch with { Name = "notes" }).IsAllDontTouch);
    }

    /// <summary>Every don't-touch integer is all ones at its own width.</summary>
    [Fact]
    public void DontTouchIsAllOnesPerFieldWidth()
    {
        StatRecord record = StatRecord.DontTouch;

        Assert.Equal(ushort.MaxValue, record.Type);
        Assert.Equal(uint.MaxValue, record.Dev);
        Assert.Equal((QidType)0xFF, record.Qid.Type);
        Assert.Equal(uint.MaxValue, record.Qid.Version);
        Assert.Equal(ulong.MaxValue, record.Qid.Path);
        Assert.Equal(uint.MaxValue, record.Mode);
        Assert.Equal(uint.MaxValue, record.ATime);
        Assert.Equal(uint.MaxValue, record.MTime);
        Assert.Equal(ulong.MaxValue, record.Length);
        Assert.Equal(string.Empty, record.Name);
        Assert.Equal(string.Empty, record.Uid);
        Assert.Equal(string.Empty, record.Gid);
        Assert.Equal(string.Empty, record.Muid);
    }

    /// <summary>
    /// The four textual fields read back as the empty string rather than null on a default record,
    /// so a decoder that never set them cannot hand a null to a handler.
    /// </summary>
    [Fact]
    public void TextualFieldsAreNeverNull()
    {
        StatRecord record = default;

        Assert.Equal(string.Empty, record.Name);
        Assert.Equal(string.Empty, record.Uid);
        Assert.Equal(string.Empty, record.Gid);
        Assert.Equal(string.Empty, record.Muid);
        Assert.Null(record.Extension);
    }

    /// <summary>
    /// STATFIXLEN (49) is the fixed part including the leading size[2] and four empty strings, so
    /// an all-empty record encodes to 47 bytes after that leading field.
    /// </summary>
    [Fact]
    public void EncodedSizeOfAnEmptyRecordAgreesWithStatFixLen()
    {
        StatRecord empty = new();

        Assert.Equal(Constants.STATFIXLEN - 2, empty.GetEncodedSize(Dialect.P9_2000));
        Assert.Equal(47, empty.GetEncodedSize(Dialect.P9_2000));
    }

    /// <summary>Each string costs its length prefix plus its UTF-8 bytes.</summary>
    [Fact]
    public void EncodedSizeCountsEveryString()
    {
        StatRecord record = new() { Name = "notes", Uid = "alice", Gid = "alice", Muid = "alice" };

        Assert.Equal(47 + 5 + 5 + 5 + 5, record.GetEncodedSize(Dialect.P9_2000));
    }

    /// <summary>
    /// A .u record additionally carries extension[s] n_uid[4] n_gid[4] n_muid[4], and a 9P2000
    /// record carries none of them however the members are set.
    /// </summary>
    [Fact]
    public void UnixFieldsCostFourteenBytesPlusTheExtension()
    {
        StatRecord record = new() { Extension = "/target", NUid = 1000, NGid = 1000, NMuid = 1000 };

        Assert.Equal(47, record.GetEncodedSize(Dialect.P9_2000));
        Assert.Equal(47 + 2 + 7 + 12, record.GetEncodedSize(Dialect.P9_2000_u));
    }
}
