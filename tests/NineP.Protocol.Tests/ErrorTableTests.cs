using NineP.Protocol;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>The fixed errno-to-ename table of the workspace architecture §3.</summary>
public sealed class ErrorTableTests
{
    /// <summary>Every row of the table, in the wording the spec fixes.</summary>
    [Theory]
    [InlineData(Errno.EPERM, "permission denied")]
    [InlineData(Errno.ENOENT, "file not found")]
    [InlineData(Errno.EIO, "i/o error")]
    [InlineData(Errno.ENXIO, "no such device or address")]
    [InlineData(Errno.EBADF, "unknown fid")]
    [InlineData(Errno.EAGAIN, "try again")]
    [InlineData(Errno.ENOMEM, "out of memory")]
    [InlineData(Errno.EACCES, "permission denied")]
    [InlineData(Errno.EEXIST, "file already exists")]
    [InlineData(Errno.ENOTDIR, "not a directory")]
    [InlineData(Errno.EISDIR, "is a directory")]
    [InlineData(Errno.EINVAL, "bad argument")]
    [InlineData(Errno.ENFILE, "too many fids")]
    [InlineData(Errno.EFBIG, "file too big")]
    [InlineData(Errno.ENOSPC, "no space left")]
    [InlineData(Errno.EROFS, "read-only file system")]
    [InlineData(Errno.ERANGE, "result too large")]
    [InlineData(Errno.ENAMETOOLONG, "file name too long")]
    [InlineData(Errno.ENOLCK, "lock not available")]
    [InlineData(Errno.ENOSYS, "not implemented")]
    [InlineData(Errno.ENOTEMPTY, "directory not empty")]
    [InlineData(Errno.ELOOP, "too many symbolic links")]
    [InlineData(Errno.ENODATA, "no such attribute")]
    [InlineData(Errno.EPROTO, "bad message")]
    [InlineData(Errno.EOVERFLOW, "value too large")]
    [InlineData(Errno.EOPNOTSUPP, "not supported")]
    [InlineData(Errno.ECONNREFUSED, "authentication not required")]
    public void EnameForMatchesTheTable(int errno, string ename) =>
        Assert.Equal(ename, ErrorTable.EnameFor(errno));

    /// <summary>The enames a server writes itself map back to an errno a .L peer understands.</summary>
    [Theory]
    [InlineData("bad message", Errno.EPROTO)]
    [InlineData("duplicate tag", Errno.EINVAL)]
    [InlineData("duplicate fid", Errno.EINVAL)]
    [InlineData("unknown fid", Errno.EBADF)]
    [InlineData("unknown message", Errno.EOPNOTSUPP)]
    [InlineData("bad offset", Errno.EINVAL)]
    [InlineData("bad open mode", Errno.EINVAL)]
    [InlineData("bad name", Errno.EINVAL)]
    [InlineData("cannot clone open fid", Errno.EINVAL)]
    [InlineData("authentication failed", Errno.EACCES)]
    [InlineData("authentication not required", Errno.ECONNREFUSED)]
    [InlineData("too many fids", Errno.ENFILE)]
    [InlineData("version not negotiated", Errno.EPROTO)]
    [InlineData("symlinks not supported", Errno.EOPNOTSUPP)]
    [InlineData("file exists", Errno.EEXIST)]
    [InlineData("directory not empty", Errno.ENOTEMPTY)]
    [InlineData("no such device or address", Errno.ENXIO)]
    [InlineData("create cannot set DMAPPEND/DMEXCL/DMTMP", Errno.EPERM)]
    [InlineData("wstat cannot change DMAPPEND/DMEXCL/DMTMP", Errno.EPERM)]
    [InlineData("wstat cannot change DMDIR", Errno.EPERM)]
    [InlineData("wstat cannot change the owner", Errno.EPERM)]
    [InlineData("wstat cannot set muid", Errno.EPERM)]
    [InlineData("wstat cannot set atime", Errno.EPERM)]
    [InlineData("wstat cannot set type", Errno.EPERM)]
    [InlineData("wstat cannot set dev", Errno.EPERM)]
    [InlineData("wstat cannot set qid", Errno.EPERM)]
    [InlineData("cannot rename across directories", Errno.EOPNOTSUPP)]
    public void ErrnoForMatchesTheTable(string ename, int errno) =>
        Assert.Equal(errno, ErrorTable.ErrnoFor(ename));

    /// <summary>
    /// Reference §8 rules 15, 19 and 23: every ename this repository writes itself has a row, so a
    /// refusal keeps its meaning in the dialect that carries only the text. A refusal built with
    /// <c>FromEname</c> and no row does not merely lose wording — it becomes <c>EIO</c>, an
    /// unspecific failure, which is what the projector and the server refusals below used to do.
    /// </summary>
    /// <param name="ename">An ename the code raises.</param>
    [Theory]
    [InlineData("no such device or address")]
    [InlineData("create cannot set DMAPPEND/DMEXCL/DMTMP")]
    [InlineData("wstat cannot change DMAPPEND/DMEXCL/DMTMP")]
    [InlineData("wstat cannot change DMDIR")]
    [InlineData("wstat cannot change the owner")]
    [InlineData("cannot rename across directories")]
    public void AWrittenEnameNeverDegradesToEio(string ename) =>
        Assert.NotEqual(Errno.EIO, ErrorTable.ErrnoFor(ename));

    /// <summary>An ename that is not ours maps to EIO rather than to a success.</summary>
    [Fact]
    public void UnknownEnameIsEio() => Assert.Equal(Errno.EIO, ErrorTable.ErrnoFor("nothing like this"));

    /// <summary>An errno that is not ours still projects to a legal Plan 9 ename.</summary>
    [Fact]
    public void UnknownErrnoProjectsToIoError() => Assert.Equal("i/o error", ErrorTable.EnameFor(9999));

    /// <summary>The published table has one row per errno the spec lists.</summary>
    [Fact]
    public void AllCarriesEveryRow()
    {
        Assert.Equal(27, ErrorTable.All.Count);
        Assert.Distinct(ErrorTable.All.Select(e => e.Errno));
    }

    /// <summary>Building an error from either half fills in the other.</summary>
    [Fact]
    public void ErrorsAreBuiltFromEitherHalf()
    {
        Assert.Equal(new NinePError("file not found", Errno.ENOENT), NinePError.FromErrno(Errno.ENOENT));
        Assert.Equal(new NinePError("too many fids", Errno.ENFILE), NinePError.FromEname("too many fids"));
    }
}
