using System.Reflection;
using NineP.Protocol;
using NineP.Protocol.Messages;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using Xunit;

namespace NineP.Protocol.Tests.Conformance;

/// <summary>The type numbers of reference §2 and the records that carry them.</summary>
[Trait("Category", "Conformance")]
public sealed class MessageTypeTests
{
    /// <summary>Every message record in the package, with the type number it declares.</summary>
    internal static IReadOnlyDictionary<MessageType, Type> Records { get; } = FindRecords();

    /// <summary>
    /// The enum declares all 68 numbers of reference §2; 66 of them have a record; and every one
    /// of the 34 replies is its request's number plus one.
    /// </summary>
    [Fact]
    public void EveryTypeHasAStruct()
    {
        MessageType[] all = Enum.GetValues<MessageType>();
        Assert.Equal(68, all.Length);
        Assert.Equal(66, Records.Count);

        foreach (MessageType type in all)
        {
            if (!MessageTypes.IsRequest(type))
            {
                continue;
            }

            Assert.Equal((byte)type + 1, (byte)MessageTypes.ReplyOf(type));
            Assert.Contains(MessageTypes.ReplyOf(type), all);
        }

        Assert.Equal(32, all.Count(MessageTypes.IsRequest));
    }

    /// <summary>
    /// Terror (106) and Tlerror (6) are declared so that the numbering is complete, but they are
    /// illegal on the wire: neither has a record and neither is in any dialect's bitmap.
    /// </summary>
    [Fact]
    public void TerrorAndTlerrorAreNotWireLegal()
    {
        foreach (MessageType illegal in new[] { MessageType.Terror, MessageType.Tlerror })
        {
            Assert.DoesNotContain(illegal, Records.Keys);
            Assert.False(MessageTypes.IsRequest(illegal));

            foreach (Dialect dialect in Enum.GetValues<Dialect>())
            {
                Assert.False(MessageTypes.IsLegal(illegal, dialect));
            }
        }

        Assert.Equal(106, (byte)MessageType.Terror);
        Assert.Equal(6, (byte)MessageType.Tlerror);
    }

    /// <summary>A record's declared type number is the one its name says.</summary>
    [Fact]
    public void EveryRecordDeclaresTheTypeItsNameSays()
    {
        foreach ((MessageType type, Type record) in Records)
        {
            Assert.Equal(record.Name, Enum.GetName(type));
        }
    }

    /// <summary>ReplyOf refuses anything that is not a request, rather than guessing.</summary>
    [Theory]
    [InlineData(MessageType.Rversion)]
    [InlineData(MessageType.Terror)]
    [InlineData(MessageType.Tlerror)]
    public void ReplyOfRefusesNonRequests(MessageType type) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MessageTypes.ReplyOf(type));

    /// <summary>The name of a type is the protocol's own spelling.</summary>
    [Fact]
    public void GetNameIsTheProtocolsSpelling()
    {
        Assert.Equal("Twalk", MessageTypes.GetName(MessageType.Twalk));
        Assert.Equal("Rgetattr", MessageTypes.GetName(MessageType.Rgetattr));
        Assert.Equal("type 200", MessageTypes.GetName((MessageType)200));
    }

    /// <summary>The tag is readable through the interface for every record, without boxing.</summary>
    [Fact]
    public void EveryRecordExposesItsTag()
    {
        Tversion version = new(Constants.NOTAG, 8192, Constants.Version9P2000);
        Rreaddir entries = new(3, ReadOnlyMemory<byte>.Empty);

        Assert.Equal(Constants.NOTAG, version.Tag);
        Assert.Equal(3, entries.Tag);
    }

    private static MessageType DeclaredType<TMessage>()
        where TMessage : struct, IMessage => TMessage.Type;

    private static Dictionary<MessageType, Type> FindRecords()
    {
        MethodInfo declared = typeof(MessageTypeTests)
            .GetMethod(nameof(DeclaredType), BindingFlags.NonPublic | BindingFlags.Static)!;

        Dictionary<MessageType, Type> records = [];
        foreach (Type candidate in typeof(IMessage).Assembly.GetTypes())
        {
            if (!candidate.IsValueType || !typeof(IMessage).IsAssignableFrom(candidate))
            {
                continue;
            }

            MessageType type = (MessageType)declared.MakeGenericMethod(candidate).Invoke(null, null)!;
            records.Add(type, candidate);
        }

        return records;
    }
}
