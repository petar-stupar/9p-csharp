using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using NineP.Server.Internal;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.StateMachine;

/// <summary>
/// Reference §8 rule 7 and §6.5: the fid rules the core owns — unknown fid, a fid that must be
/// fresh, the per-connection cap, and the gate that makes one fid a state machine.
/// </summary>
[Trait("Category", "StateMachine")]
public sealed class FidTableTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Rule 48: the table is bounded, and going over the cap is <b>one</b> condition with one
    /// answer — <c>"too many fids"</c> in 9P2000 and .u, <c>ENFILE (23)</c> in .L. The two are the
    /// same error value seen through the two projections, which is what keeps them from drifting.
    /// </summary>
    /// <param name="dialect">The session dialect the refusal is projected into.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(Dialect.P9_2000)]
    [InlineData(Dialect.P9_2000_u)]
    [InlineData(Dialect.P9_2000_L)]
    public async Task CapOverflow(Dialect dialect)
    {
        await using FidTable table = new(4);
        MemoryFilesystem tree = new();

        for (uint fid = 0; fid < 4; fid++)
        {
            table.Bind(Entry(fid, tree));
        }

        NinePException overflow = Assert.Throws<NinePException>(() => table.Bind(Entry(4, tree)));

        Assert.Equal(Errno.ENFILE, overflow.Error.Errno);
        Assert.Equal("Too many open files in system", overflow.Error.Ename);

        if (dialect == Dialect.P9_2000_L)
        {
            Assert.Equal(Errno.ENFILE, ErrorProjector.ToRlerror(1, overflow.Error).Ecode);
            return;
        }

        Rerror projected = ErrorProjector.ToRerror(1, overflow.Error, dialect);
        Assert.Equal("Too many open files in system", projected.Ename);
        Assert.Equal(dialect == Dialect.P9_2000_u ? Errno.ENFILE : 0, projected.Errno);
    }

    /// <summary>Rule 48: a fid the connection does not hold is <c>"unknown fid"</c> / <c>EBADF</c>.</summary>
    [Fact]
    public async Task UnknownFidRejected()
    {
        await using FidTable table = new(16);

        NinePException unknown = Assert.Throws<NinePException>(() => table.Get(7));

        Assert.Equal(Errno.EBADF, unknown.Error.Errno);
        Assert.Equal("fid unknown or out of range", unknown.Error.Ename);
    }

    /// <summary>A number that must be fresh is refused when it is in use; <c>newfid == fid</c> is not.</summary>
    [Fact]
    public async Task DuplicateFidRejected()
    {
        await using FidTable table = new(16);
        MemoryFilesystem tree = new();
        table.Bind(Entry(1, tree));

        NinePException duplicate = Assert.Throws<NinePException>(() => table.RequireFree(1, 2));
        Assert.Equal("duplicate fid", duplicate.Error.Ename);
        Assert.Equal(Errno.EINVAL, duplicate.Error.Errno);

        // walk(5) allows newfid == fid, which is the one case that is not a duplicate.
        table.RequireFree(1, 1);
    }

    /// <summary>NOFID never names a fid a client may bind; it means "no fid at all".</summary>
    [Fact]
    public async Task NofidCannotBeBound()
    {
        await using FidTable table = new(16);
        MemoryFilesystem tree = new();

        NinePException refusal =
            Assert.Throws<NinePException>(() => table.Bind(Entry(Constants.NOFID, tree)));

        Assert.Equal("duplicate fid", refusal.Error.Ename);
    }

    /// <summary>
    /// §6.5: operations on one fid are serialised — a fid is a state machine — while different
    /// fids run concurrently. The second waiter cannot enter until the first has left.
    /// </summary>
    [Fact]
    public async Task PerFidOperationsSerialise()
    {
        await using FidTable table = new(16);
        MemoryFilesystem tree = new();

        FidEntry one = table.Bind(Entry(1, tree));
        FidEntry two = table.Bind(Entry(2, tree));

        await one.Gate.WaitAsync(Ct);

        Task blocked = one.Gate.WaitAsync(Ct);
        Assert.False(blocked.IsCompleted);

        // A different fid is unaffected: that is what "different fids run concurrently" means.
        Assert.True(await two.Gate.WaitAsync(TimeSpan.FromSeconds(1), Ct));
        two.Gate.Release();

        one.Gate.Release();
        await blocked;
        one.Gate.Release();
    }

    /// <summary>Clearing the table releases every handler, which is what a Tversion reset does.</summary>
    [Fact]
    public async Task ClearReleasesEveryHandler()
    {
        await using FidTable table = new(16);
        MemoryFilesystem tree = new();
        MemoryFile file = tree.NewFile("a", Perms.P0644);

        // CA2000: ClearAsync releases every entry it removes, which is the whole assertion here.
#pragma warning disable CA2000
        table.Bind(new FidEntry(1, file, Identity.Anonymous("glenda"), string.Empty));
#pragma warning restore CA2000
        await table.ClearAsync(Ct);

        Assert.Equal(0, table.Count);
        Assert.True(file.WasClunked);
    }

    private static FidEntry Entry(uint fid, MemoryFilesystem tree) =>
        new(fid, tree.Root, Identity.Anonymous("glenda"), string.Empty);
}
