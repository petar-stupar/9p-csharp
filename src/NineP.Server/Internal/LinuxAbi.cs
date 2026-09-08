namespace NineP.Server.Internal;

/// <summary>
/// The one Linux ABI value this server has to know and <c>NineP.Protocol</c> does not publish: the
/// <c>AT_*</c> flag word of <c>Tunlinkat</c> (reference §4.5, <c>linux-9p.h:317</c>). It does not
/// belong in the protocol package's vocabulary — only the server interprets it — so it is written
/// out here once and pinned by the tests that use it. <c>ENXIO</c>, which rule 23 names, used to
/// live here for the same reason and moved to <see cref="NineP.Protocol.Errno"/>: it travels to a
/// peer, so it needs the Plan 9 wording <see cref="NineP.Protocol.ErrorTable"/> gives it.
/// </summary>
internal static class LinuxAbi
{
    // IDE1006: AT_REMOVEDIR is the spelling of <fcntl.h> and of the reference, and the naming rule
    // wants no underscore; the constant is worth more with the name the kernel gives it.
#pragma warning disable IDE1006
    /// <summary>Remove a directory rather than a file (0x200): the one flag <c>Tunlinkat</c> defines.</summary>
    public const uint AT_REMOVEDIR = 0x200;
#pragma warning restore IDE1006
}
