using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>
/// The whole of reference §9: all 77 golden frames decode into their typed records in the dialect
/// the fixture recorded them under, re-encode, and compare byte for byte. The fixture is read-only
/// truth shared by every implementation in the workspace and is never regenerated here.
/// </summary>
public sealed class GoldenVectorTests
{
    private const int VectorCount = 77;
    private const int DistinctMessageNames = 66;
    private const int RgetattrFrameSize = 160;

    /// <summary>Every vector in the fixture, one test case each.</summary>
    /// <returns>One row per vector.</returns>
    public static TheoryData<string, int> AllVectors()
    {
        TheoryData<string, int> data = [];
        for (int i = 0; i < WireVectors.All.Count; i++)
        {
            data.Add(WireVectors.All[i].Name, i);
        }

        return data;
    }

    /// <summary>Every vector decodes and re-encodes to exactly the bytes it came from.</summary>
    /// <param name="name">The vector's name, so a failure says which one broke.</param>
    /// <param name="index">The vector's position in the fixture.</param>
    [Theory]
    [MemberData(nameof(AllVectors))]
    public void EveryVectorRoundTripsByteExactly(string name, int index)
    {
        WireVector vector = WireVectors.All[index];

        Assert.Equal(vector.Size, vector.Frame.Length);
        Assert.Equal((uint)vector.Size, MessageCodec.PeekSize(vector.Frame.Span));
        Assert.Equal(vector.Type, MessageCodec.PeekType(vector.Frame.Span));
        Assert.Equal((ushort)vector.Num("tag"), MessageCodec.PeekTag(vector.Frame.Span));

        // The short Tfsync is the one frame that does not survive a round trip unchanged: the
        // encoder always writes datasync[4], which is exactly what S-19 asks for.
        byte[] expected = name == "Tfsync (no datasync)"
            ? WireVectors.All.First(v => v.Type == MessageType.Tfsync && v.Name == "Tfsync").ToBytes()
            : vector.ToBytes();

        Assert.Equal(expected.Length, VectorCodec.PredictedSize(vector));
        Assert.Equal(expected, VectorCodec.ReEncode(vector));
    }

    /// <summary>The fixture holds exactly the 77 frames reference §9 promises.</summary>
    [Fact]
    public void FixtureHoldsSeventySevenVectors() => Assert.Equal(VectorCount, WireVectors.All.Count);

    /// <summary>
    /// The 77 frames cover 66 distinct message names — every wire-legal type of reference §2 — so
    /// a type that is missing cannot hide behind a type that appears twice.
    /// </summary>
    [Fact]
    public void VectorsCoverSixtySixDistinctMessageNames()
    {
        HashSet<string> names = [.. WireVectors.All.Select(BaseName)];

        Assert.Equal(DistinctMessageNames, names.Count);
        foreach (MessageType type in MessageRecords.ByType.Keys)
        {
            Assert.Contains(MessageTypes.GetName(type), names);
        }

        Assert.Equal(DistinctMessageNames, MessageRecords.ByType.Count);
    }

    /// <summary>Every record the fixture names decodes in the dialect the fixture recorded.</summary>
    [Fact]
    public void EveryVectorIsLegalInItsOwnDialect()
    {
        foreach (WireVector vector in WireVectors.All)
        {
            Assert.True(
                MessageTypes.IsLegal(vector.Type, vector.Dialect),
                $"{vector.Name} is not legal in {vector.Dialect}");
        }
    }

    /// <summary>The Rgetattr frame is 160 bytes exactly (reference §3.4, §4.6).</summary>
    [Fact]
    public void RgetattrIs160Bytes()
    {
        WireVector vector = WireVectors.All.First(v => v.Type == MessageType.Rgetattr);

        Assert.Equal(RgetattrFrameSize, vector.Size);
        Assert.Equal(RgetattrFrameSize, VectorCodec.PredictedSize(vector));
        Assert.Equal(RgetattrFrameSize, VectorCodec.ReEncode(vector).Length);
    }

    private static string BaseName(WireVector vector)
    {
        int space = vector.Name.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? vector.Name : vector.Name[..space];
    }
}
