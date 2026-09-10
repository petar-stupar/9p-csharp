using NineP.Protocol;
using NineP.Protocol.Messages;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Regression;

[Trait("Category", "Regression")]
public sealed class FlushBackpressureRegressionTests
{
    [Fact]
    public async Task FlushBehindOneQueuedOrdinaryRequestMustReachItsReservedSlot()
    {
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(10));
        CancellationToken ct = budget.Token;
        MemoryFilesystem tree = new();
        MemoryFile file = tree.NewFile("slow", 0x1A4);
        file.Data = "content"u8.ToArray();
        file.ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        tree.Root.Add(file);
        await using ServerHarness harness = await ServerHarness.StartAsync(
            options => options with
            {
                Limits = Limits.Default with
                {
                    MaxInFlightPerConnection = 2,
                    FlushReservePerConnection = 1,
                    MaxInFlightPerListener = 8
                }
            }, tree);
        await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, 8192, ct);
        await client.AttachAsync(1, ct);
        for (uint fid = 2; fid <= 3; fid++)
        {
            await client.WalkAsync((ushort)fid, 1, fid, ["slow"], ct);
            await client.SendAsync(new Tlopen((ushort)(fid + 100), fid, 0), ct);
            await client.ReceiveAsync<Rlopen>(ct);
        }
        await client.SendAsync(new Tread(10, 2, 0, 16), ct);
        while (file.ReadsStarted < 1)
        {
            await Task.Delay(5, ct);
        }
        await client.SendAsync(new Tread(11, 3, 0, 16), ct);
        await client.SendAsync(new Tflush(12, 10), ct);
        try
        {
            Rlerror excess = await client.ReceiveAsync<Rlerror>(ct).WaitAsync(TimeSpan.FromSeconds(1), ct);
            Assert.Equal(11, excess.Tag);
            Assert.Equal(Errno.EAGAIN, excess.Ecode);
            Rflush reply = await client.ReceiveAsync<Rflush>(ct).WaitAsync(TimeSpan.FromSeconds(1), ct);
            Assert.Equal(12, reply.Tag);
        }
        finally
        {
            file.ReadGate.TrySetResult();
        }
    }
}
