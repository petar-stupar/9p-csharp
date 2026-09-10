using NineP.Client;
using NineP.Protocol;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Conformance;

/// <summary>Reference §4.9: what a synthetic server reports for <c>Tstatfs</c>.</summary>
[Trait("Category", "Conformance")]
public sealed class StatFsTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Rule 18: a synthetic filesystem reports <c>V9FS_MAGIC</c> and a name length of 255, which
    /// is what a Linux client checks the mount against.
    /// </summary>
    [Fact]
    public async Task SyntheticServersReportV9fsMagic()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        StatFs statistics = await session.StatFsAsync("/", Ct);

        Assert.Equal(0x01021997u, statistics.Type);
        Assert.Equal(StatFs.V9fsMagic, statistics.Type);
        Assert.Equal((uint)Constants.MaxNameLength, statistics.NameLength);
    }

    /// <summary>The filesystem answers when no handler carries the capability itself.</summary>
    [Fact]
    public async Task TheFilesystemAnswersWhenTheHandlerDoesNot()
    {
        MemoryFilesystem tree = new();
        tree.Root.Add(tree.NewFile("plain", 0x1B6));

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        // MemoryFile carries no IStatFsCapability; the tree does, and the core falls back to it.
        StatFs statistics = await session.StatFsAsync("plain", Ct);

        Assert.Equal(StatFs.V9fsMagic, statistics.Type);
    }

    /// <summary>Statfs is a .L message; the other dialects cannot carry it at all.</summary>
    [Fact]
    public async Task StatfsIsLinuxOnly()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_u);

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await session.StatFsAsync("/", Ct));

        Assert.Equal(Errno.EOPNOTSUPP, refusal.Error.Errno);
    }
}
