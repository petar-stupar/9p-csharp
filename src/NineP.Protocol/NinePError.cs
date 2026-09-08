using System.Text;

namespace NineP.Protocol;

/// <summary>
/// The 9P error value: a Plan 9 ename plus a Linux errno (workspace architecture §3). One value,
/// three wire shapes — <c>Rerror</c>, <c>Rerror</c> with <c>errno[4]</c>, and <c>Rlerror</c> —
/// chosen by the session dialect, never by the code that raised it.
/// </summary>
/// <param name="Ename">The Plan 9 error text a 9P2000 or 9P2000.u peer is told.</param>
/// <param name="Errno">The Linux errno a 9P2000.L peer is told.</param>
public readonly record struct NinePError(string Ename, int Errno)
{
    /// <summary>Builds an error from an errno, taking the ename from <see cref="ErrorTable"/>.</summary>
    /// <param name="errno">The Linux errno.</param>
    /// <returns>The error value carrying both projections.</returns>
    public static NinePError FromErrno(int errno) => new(ErrorTable.EnameFor(errno), errno);

    /// <summary>Builds an error from an ename, taking the errno from <see cref="ErrorTable"/>.</summary>
    /// <param name="ename">The Plan 9 error text; an unknown one maps to <c>EIO</c>.</param>
    /// <returns>The error value carrying both projections.</returns>
    public static NinePError FromEname(string ename) => new(ename, ErrorTable.ErrnoFor(ename));

    /// <summary>
    /// The ename truncated to <c>ERRMAX - 1</c> bytes on a UTF-8 rune boundary, so a long error
    /// never overruns the conventional cap and never splits a character (reference §8 rule 10).
    /// </summary>
    public string TruncatedEname => Truncate(Ename, Constants.ERRMAX - 1);

    private static string Truncate(string value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        int chars = 0;
        int bytes = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            int width = rune.Utf8SequenceLength;
            if (bytes + width > maxBytes)
            {
                return value[..chars];
            }

            bytes += width;
            chars += rune.Utf16SequenceLength;
        }

        return value;
    }
}
