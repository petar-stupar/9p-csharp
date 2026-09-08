using NineP.Protocol.Messages;

namespace NineP.Protocol.Internal;

/// <summary>The configurable name policy, separate from the codec's hard 255-byte bound.</summary>
internal static class RequestNameLimit
{
    public static void Validate<TMessage>(in TMessage message, int maximum)
        where TMessage : struct, IMessage
    {
        switch (message)
        {
            case Twalk walk:
                foreach (string name in walk.Wnames)
                {
                    ValidateName(name, maximum);
                }
                break;
            case Tcreate create: ValidateName(create.Name, maximum); break;
            case Tlcreate create: ValidateName(create.Name, maximum); break;
            case Tmkdir create: ValidateName(create.Name, maximum); break;
            case Tsymlink create: ValidateName(create.Name, maximum); break;
            case Tmknod create: ValidateName(create.Name, maximum); break;
            case Tlink link: ValidateName(link.Name, maximum); break;
            case Trename rename: ValidateName(rename.Name, maximum); break;
            case Trenameat rename:
                ValidateName(rename.OldName, maximum);
                ValidateName(rename.NewName, maximum);
                break;
            case Tunlinkat unlink: ValidateName(unlink.Name, maximum); break;
            case Twstat stat: ValidateName(stat.Stat.Name, maximum); break;
            case Txattrwalk walk: ValidateName(walk.Name, maximum); break;
            case Txattrcreate create: ValidateName(create.Name, maximum); break;
        }
    }

    internal static void ValidateName(string name, int maximum)
    {
        // Malformed names remain codec errors. This policy refuses only wire-legal names.
        int length = NinePText.Utf8.GetByteCount(name);
        if (length > Constants.MaxNameLength)
        {
            throw new NinePProtocolException(ProtocolErrorKind.Name, "a name exceeds 255 UTF-8 bytes");
        }
        if (NinePText.IsLegalName(name, allowParent: true)
            && !name.Contains('\0', StringComparison.Ordinal)
            && length <= Constants.MaxNameLength && length > maximum)
        {
            throw new NinePException(NinePError.FromErrno(Errno.ENAMETOOLONG));
        }
    }
}
