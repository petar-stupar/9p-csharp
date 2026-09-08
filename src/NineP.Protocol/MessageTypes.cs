using System.Globalization;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;

namespace NineP.Protocol;

/// <summary>
/// Helpers over <see cref="MessageType"/>: wire legality per dialect, T/R pairing, and the
/// protocol's own name for a type number.
/// </summary>
public static class MessageTypes
{
    private static readonly string?[] Names = BuildNames();
    private static readonly bool[] Defined = BuildDefined();

    /// <summary>
    /// True when the type may appear in a session of that dialect (reference §2). <c>Terror</c>
    /// and <c>Tlerror</c> are legal in none.
    /// </summary>
    /// <param name="type">The type number peeked out of the frame.</param>
    /// <param name="dialect">The dialect the session negotiated.</param>
    /// <returns>False for an unknown number and for one the dialect does not carry.</returns>
    public static bool IsLegal(MessageType type, Dialect dialect) => DialectLegality.IsLegal(type, dialect);

    /// <summary>
    /// True for a request type, false for a reply. <c>Terror</c> and <c>Tlerror</c> are neither:
    /// they are declared so that the numbers are accounted for, and they never reach the wire.
    /// </summary>
    /// <param name="type">The type number to classify.</param>
    /// <returns>True only for a T-message that may be sent.</returns>
    public static bool IsRequest(MessageType type) =>
        IsDefined(type) &&
        type is not MessageType.Terror and not MessageType.Tlerror &&
        ((byte)type & 1) == 0;

    /// <summary>The reply type paired with a request type, which is always its number plus one.</summary>
    /// <param name="requestType">The request type.</param>
    /// <returns>The type of the reply that answers it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The argument is not a request type.</exception>
    public static MessageType ReplyOf(MessageType requestType) =>
        IsRequest(requestType)
            ? (MessageType)((byte)requestType + 1)
            : throw new ArgumentOutOfRangeException(
                nameof(requestType), requestType, "not a request type");

    /// <summary>
    /// The protocol's name for the type, for example "Twalk", used in logs and error text.
    /// </summary>
    /// <param name="type">The type number to name.</param>
    /// <returns>The protocol name, or "type &lt;n&gt;" for a number the protocol does not define.</returns>
    public static string GetName(MessageType type) =>
        Names[(byte)type] ?? string.Format(CultureInfo.InvariantCulture, "type {0}", (byte)type);

    private static bool IsDefined(MessageType type) => Defined[(byte)type];

    private static string?[] BuildNames()
    {
        string?[] names = new string?[256];
        foreach (MessageType type in Enum.GetValues<MessageType>())
        {
            names[(byte)type] = Enum.GetName(type);
        }

        return names;
    }

    private static bool[] BuildDefined()
    {
        bool[] defined = new bool[256];
        foreach (MessageType type in Enum.GetValues<MessageType>())
        {
            defined[(byte)type] = true;
        }

        return defined;
    }
}
