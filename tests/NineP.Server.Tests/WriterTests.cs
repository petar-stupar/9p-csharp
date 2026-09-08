using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;
using NineP.Server.Internal;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests;

/// <summary>
/// The single writer per connection (architecture §4, RK-58). <c>SslStream</c> and
/// <c>WebSocket</c> both forbid concurrent writes, and a 9P frame is written whole or the
/// connection is desynchronised, so every reply goes out through one channel and one task.
/// </summary>
public sealed class WriterTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// A hundred handlers finishing at once produce a hundred whole frames. A writer that let two
    /// replies interleave would produce frames that do not decode at all, which is what reading
    /// them back one at a time proves did not happen.
    /// </summary>
    [Fact]
    public async Task ConcurrentRepliesAreSerialised()
    {
        const int replies = 100;

        (INinePConnection client, INinePConnection server) = MemoryTransport.CreatePair();
        await using (client.ConfigureAwait(false))
        {
            ServerOptions options = new()
            {
                Listen = [new NinePAddress(NinePScheme.Memory, "writer", 0, string.Empty)],
            };

            ServerSession session = new(server, options, new MemoryFilesystem(), new ServerMetrics(), new OpenState());
            await using (session.ConfigureAwait(false))
            {
                Task running = session.RunAsync(Ct);

                await Task.WhenAll(Enumerable.Range(0, replies).Select(async tag =>
                {
                    await Task.Yield();
                    await session.EnqueueAsync(new Rclunk((ushort)tag), Dialect.P9_2000);
                }));

                HashSet<ushort> seen = [];
                for (int i = 0; i < replies; i++)
                {
                    Rclunk reply = await ReadAsync(client);
                    Assert.True(seen.Add(reply.Tag), "a reply arrived twice");
                }

                Assert.Equal(replies, seen.Count);
                await client.CloseAsync(CloseReason.Normal, Ct);
                await running;
            }
        }
    }

    private static async Task<Rclunk> ReadAsync(INinePConnection wire)
    {
        byte[] header = await ReadExactlyAsync(wire, 4);
        uint size = MessageCodec.PeekSize(header);
        byte[] rest = await ReadExactlyAsync(wire, (int)size - 4);

        byte[] frame = new byte[size];
        header.CopyTo(frame, 0);
        rest.CopyTo(frame, 4);

        return MessageCodec.Decode<Rclunk>(frame, Dialect.P9_2000);
    }

    private static async Task<byte[]> ReadExactlyAsync(INinePConnection wire, int count)
    {
        byte[] buffer = new byte[count];
        int filled = 0;

        while (filled < count)
        {
            int read = await wire.ReadAsync(buffer.AsMemory(filled), TestDeadlines.Wrap(TestContext.Current.CancellationToken));
            Assert.NotEqual(0, read);
            filled += read;
        }

        return buffer;
    }
}
