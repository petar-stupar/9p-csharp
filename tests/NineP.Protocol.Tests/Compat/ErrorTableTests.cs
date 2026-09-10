using System.Text.Json;
using NineP.Protocol;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests.Compat;

/// <summary>
/// The error table against the strings the Linux kernel's 9P client accepts over plain 9P2000
/// (<c>docs/9p/fixtures/linux-9p-errors.json</c>, generated from <c>net/9p/error.c</c>). Over
/// 9P2000 an <c>Rerror</c> carries only the ename and v9fs maps it with an exact-match table, so
/// the ename sent for an errno must be one Linux maps to that errno, and every string Linux
/// knows must map back to its errno here. Found by mounting <c>jsonfs</c> from v9fs
/// (docs/interop.md, 2026-09-10): a refused write read as error 526.
/// </summary>
[Trait("Category", "Compat")]
public sealed class ErrorTableTests
{
    /// <summary>Errnos the Linux table does not name; the ename sent for them cannot be checked against it.</summary>
    private static readonly HashSet<int> AbsentFromLinux = [Errno.EOVERFLOW];

    /// <summary>
    /// Strings this table maps differently from Linux on purpose: reference §5.2 answers a failed
    /// authentication <c>EACCES</c>, where Linux files the same words under <c>ECONNREFUSED</c>.
    /// </summary>
    private static readonly Dictionary<string, int> DeliberateOverrides = new(StringComparer.Ordinal)
    {
        ["authentication failed"] = Errno.EACCES,
    };

    private static readonly Lazy<Dictionary<int, string[]>> Fixture = new(LoadFixture);

    /// <summary>The ename sent for every errno is one Linux maps to exactly that errno.</summary>
    [Fact]
    public void EverySentEnameIsOneLinuxMapsToThatErrno()
    {
        List<string> wrong = [];

        foreach (NinePError row in ErrorTable.All)
        {
            if (AbsentFromLinux.Contains(row.Errno))
            {
                continue;
            }

            Assert.True(Fixture.Value.TryGetValue(row.Errno, out string[]? accepted), $"errno {row.Errno} is not in the Linux table");
            if (!accepted.Contains(row.Ename, StringComparer.Ordinal))
            {
                wrong.Add($"{row.Errno}: \"{row.Ename}\" is not among {string.Join(", ", accepted.Select(a => '"' + a + '"'))}");
            }
        }

        Assert.Empty(wrong);
    }

    /// <summary>Every string Linux maps to an errno maps to the same errno here, bar the documented overrides.</summary>
    [Fact]
    public void EveryLinuxEnameMapsBackToItsErrno()
    {
        List<string> wrong = [];

        foreach ((int errno, string[] enames) in Fixture.Value)
        {
            foreach (string ename in enames)
            {
                int expected = DeliberateOverrides.TryGetValue(ename, out int overridden) ? overridden : errno;
                if (ErrorTable.ErrnoFor(ename) != expected)
                {
                    wrong.Add($"\"{ename}\" -> {ErrorTable.ErrnoFor(ename)}, Linux says {errno}");
                }
            }
        }

        Assert.Empty(wrong);
    }

    /// <summary>The embedded copy of the Linux table is the fixture, row for row.</summary>
    [Fact]
    public void LinuxTableMatchesTheFixture()
    {
        Dictionary<int, string[]> embedded = ErrorTable.LinuxTable.ToDictionary(row => row.Errno, row => row.Enames);

        Assert.Equal(Fixture.Value.Keys.Order(), embedded.Keys.Order());
        foreach ((int errno, string[] enames) in Fixture.Value)
        {
            Assert.Equal(enames, embedded[errno]);
        }
    }

    /// <summary>Every errno the Linux table names has a constant of the same number, and the table sends for it.</summary>
    [Fact]
    public void EveryLinuxErrnoIsAConstantAndARow()
    {
        Dictionary<string, int> constants = typeof(Errno).GetFields()
            .Where(field => field.IsLiteral)
            .ToDictionary(field => field.Name, field => (int)field.GetRawConstantValue()!, StringComparer.Ordinal);
        HashSet<int> rows = [.. ErrorTable.All.Select(row => row.Errno)];

        foreach ((string name, int errno) in FixtureNames.Value)
        {
            Assert.True(constants.TryGetValue(name, out int value) && value == errno, $"Errno.{name} = {errno} is missing");
            Assert.Contains(errno, rows);
        }
    }

    /// <summary>The enames sent before the alignment are still understood, and never sent.</summary>
    [Theory]
    [InlineData("unknown fid", Errno.EBADF)]
    [InlineData("too many fids", Errno.ENFILE)]
    [InlineData("bad argument", Errno.EINVAL)]
    [InlineData("read-only file system", Errno.EROFS)]
    [InlineData("is a directory", Errno.EISDIR)]
    [InlineData("directory not empty", Errno.ENOTEMPTY)]
    [InlineData("not supported", Errno.EOPNOTSUPP)]
    [InlineData("bad message", Errno.EPROTO)]
    [InlineData("no such device or address", Errno.ENXIO)]
    [InlineData("try again", Errno.EAGAIN)]
    [InlineData("create cannot set DMAPPEND/DMEXCL/DMTMP", Errno.EPERM)]
    [InlineData("wstat cannot change DMAPPEND/DMEXCL/DMTMP", Errno.EPERM)]
    public void AFormerEnameIsStillUnderstoodButNotSent(string former, int errno)
    {
        Assert.Equal(errno, ErrorTable.ErrnoFor(former));
        Assert.NotEqual(former, ErrorTable.EnameFor(errno));
    }

    /// <summary>The wordings this project chose where Linux lists a Plan 9 one beside strerror's.</summary>
    [Theory]
    [InlineData(Errno.EPERM, "Operation not permitted")]
    [InlineData(Errno.ENOENT, "file not found")]
    [InlineData(Errno.EIO, "i/o error")]
    [InlineData(Errno.EBADF, "fid unknown or out of range")]
    [InlineData(Errno.EAGAIN, "Resource temporarily unavailable")]
    [InlineData(Errno.EACCES, "permission denied")]
    [InlineData(Errno.EEXIST, "file already exists")]
    [InlineData(Errno.ENOTDIR, "not a directory")]
    [InlineData(Errno.EISDIR, "Is a directory")]
    [InlineData(Errno.EINVAL, "Invalid argument")]
    [InlineData(Errno.ENFILE, "Too many open files in system")]
    [InlineData(Errno.EFBIG, "file too big")]
    [InlineData(Errno.EROFS, "Read-only file system")]
    [InlineData(Errno.EPROTO, "protocol botch")]
    [InlineData(Errno.EOPNOTSUPP, "Operation not supported")]
    [InlineData(Errno.ECONNREFUSED, "Connection refused")]
    [InlineData(Errno.EOVERFLOW, "Value too large for defined data type")]
    public void EnameForMatchesTheTable(int errno, string ename) =>
        Assert.Equal(ename, ErrorTable.EnameFor(errno));

    /// <summary>"permission denied" is shared by EPERM and EACCES on the way in, and maps back to EACCES.</summary>
    [Fact]
    public void PermissionDeniedMapsBackToEacces() => Assert.Equal(Errno.EACCES, ErrorTable.ErrnoFor("permission denied"));

    [Theory]
    [InlineData("duplicate tag", Errno.EINVAL)]
    [InlineData("duplicate fid", Errno.EINVAL)]
    [InlineData("unknown message", Errno.EOPNOTSUPP)]
    [InlineData("bad offset", Errno.EINVAL)]
    [InlineData("bad open mode", Errno.EINVAL)]
    [InlineData("bad name", Errno.EINVAL)]
    [InlineData("cannot clone open fid", Errno.EINVAL)]
    [InlineData("authentication failed", Errno.EACCES)]
    [InlineData("authentication not required", Errno.ECONNREFUSED)]
    [InlineData("version not negotiated", Errno.EPROTO)]
    [InlineData("symlinks not supported", Errno.EOPNOTSUPP)]
    [InlineData("file exists", Errno.EEXIST)]
    [InlineData("wstat cannot change DMDIR", Errno.EPERM)]
    [InlineData("wstat cannot set DMAUTH or DMMOUNT", Errno.EPERM)]
    [InlineData("create cannot set DMAUTH or DMMOUNT", Errno.EPERM)]
    [InlineData("wstat cannot change the owner", Errno.EPERM)]
    [InlineData("wstat cannot set muid", Errno.EPERM)]
    [InlineData("wstat cannot set atime", Errno.EPERM)]
    [InlineData("wstat cannot set type", Errno.EPERM)]
    [InlineData("wstat cannot set dev", Errno.EPERM)]
    [InlineData("wstat cannot set qid", Errno.EPERM)]
    [InlineData("cannot rename across directories", Errno.EOPNOTSUPP)]
    public void ServerEnamesMapBack(string ename, int errno) =>
        Assert.Equal(errno, ErrorTable.ErrnoFor(ename));

    [Theory]
    [InlineData("create cannot set DMAUTH or DMMOUNT")]
    [InlineData("wstat cannot set DMAUTH or DMMOUNT")]
    [InlineData("wstat cannot change DMDIR")]
    [InlineData("wstat cannot change the owner")]
    [InlineData("cannot rename across directories")]
    public void AWrittenEnameNeverDegradesToEio(string ename) =>
        Assert.NotEqual(Errno.EIO, ErrorTable.ErrnoFor(ename));

    [Fact]
    public void UnknownEnameIsEio() => Assert.Equal(Errno.EIO, ErrorTable.ErrnoFor("nothing like this"));

    [Fact]
    public void UnknownErrnoProjectsToIoError() => Assert.Equal("i/o error", ErrorTable.EnameFor(9999));

    [Fact]
    public void AllCarriesEveryRow()
    {
        Assert.Equal(Fixture.Value.Count + AbsentFromLinux.Count, ErrorTable.All.Count);
        Assert.Equal(ErrorTable.All.Count, ErrorTable.All.Select(row => row.Errno).Distinct().Count());
    }

    private static readonly Lazy<List<(string Name, int Errno)>> FixtureNames = new(() =>
        [.. Rows().Select(row => (row.GetProperty("name").GetString()!, row.GetProperty("errno").GetInt32()))]);

    private static Dictionary<int, string[]> LoadFixture() =>
        Rows().ToDictionary(
            row => row.GetProperty("errno").GetInt32(),
            row => row.GetProperty("strings").EnumerateArray().Select(s => s.GetString()!).ToArray());

    private static List<JsonElement> Rows()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(RepositoryPaths.Combine("docs", "9p", "fixtures", "linux-9p-errors.json")));
        return [.. document.RootElement.GetProperty("errnos").EnumerateArray().Select(row => row.Clone())];
    }
}
