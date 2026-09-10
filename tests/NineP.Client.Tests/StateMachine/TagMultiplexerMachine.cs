using NineP.Protocol;
using NineP.Protocol.Messages;
using NineP.TestSupport;
using Xunit;

namespace NineP.Client.Tests.StateMachine;

[Trait("Category", "StateMachine")]
public sealed class TagMultiplexerMachine
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(17u)]
    [InlineData(947u)]
    [InlineData(65537u)]
    public async Task GeneratedReplyAndFlushSchedulesPreserveTagOwnership(uint seed)
    {
        seed = ModelSequences.Seed(seed);
        uint[] commands = ModelSequences.Generate(seed, 7, [0, 0, 2, 0, 1, 4, 0, 5, 0, 3, 0, 6]);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NINEP_MODEL_COMMANDS")))
        {
            Assert.Equal(7, commands.Select(c => c & 255).Distinct().Count());
        }
        await ModelSequences.VerifyAsync(seed, commands, ExecuteAsync, Ct);
    }

    private static async Task ExecuteAsync(IReadOnlyList<uint> commands)
    {
        await using Machine machine = await Machine.StartAsync();
        foreach (uint command in commands)
        {
            int operation = (int)(command & 255);
            if (machine.Pending.Count == 0 && operation is not (0 or 6))
            {
                await machine.IssueAsync();
            }
            switch (operation)
            {
                case 0:
                    await machine.IssueAsync();
                    break;
                case 1:
                    await machine.CompleteAsync(machine.Select(command));
                    break;
                case 2:
                    await machine.CancelAsync(machine.Select(command), (command & 256) != 0);
                    break;
                case 3:
                    foreach (Call call in machine.Pending.Values.Reverse().ToArray())
                    {
                        await machine.CompleteAsync(call);
                    }
                    break;
                case 4:
                    await machine.CompleteAsync(machine.Select(command), error: true);
                    break;
                case 5:
                    {
                        Call completed = machine.Select(command);
                        await machine.CompleteAsync(completed);
                        await completed.Cancel.CancelAsync();
                        // If cancellation leaked a flush after completion, the next read is not Tclunk.
                        await machine.IssueAsync();
                        break;
                    }
                case 6:
                    await machine.ResetAsync();
                    break;
            }
            Assert.Equal(machine.Pending.Count, machine.Session.Multiplexer.TagsInUse);
            Assert.All(machine.Pending.Values, call => Assert.False(call.Reply.IsCompleted));
        }
        await machine.ResetAsync();
        foreach (Call call in machine.All)
        {
            await call.Observed.WaitAsync(Ct);
            Assert.Equal(1, call.Completions);
        }
    }

    private sealed class Call(ushort tag, Task<Rclunk> reply, CancellationTokenSource cancel)
    {
        private int _completions;
        public ushort Tag { get; } = tag;
        public Task<Rclunk> Reply { get; } = reply;
        public CancellationTokenSource Cancel { get; } = cancel;
        public int Completions => Volatile.Read(ref _completions);
        public Task Observed { get; private set; } = Task.CompletedTask;
        public void Observe() => Observed = Reply.ContinueWith(_ => Interlocked.Increment(ref _completions),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private sealed class Machine(NinePSession session, FakeNinePServer server) : IAsyncDisposable
    {
        public NinePSession Session { get; private set; } = session;
        private FakeNinePServer Server { get; set; } = server;
        public Dictionary<ushort, Call> Pending { get; } = [];
        public List<Call> All { get; } = [];
        private uint _fid;
        private readonly List<CancellationTokenSource> _cancellations = [];

        public static async Task<Machine> StartAsync()
        {
            var (session, server) = await ConnectAsync();
            return new Machine(session, server);
        }

        private static async Task<(NinePSession Session, FakeNinePServer Server)> ConnectAsync()
        {
            var (wire, server) = FakeNinePServer.CreatePair();
            Task<NinePSession> connecting = NinePClient.ConnectAsync(wire,
                new ClientOptions { Dialects = [Dialect.P9_2000_L], RequestTimeout = TimeSpan.FromSeconds(30) }, Ct).AsTask();
            await server.NegotiateAsync(Constants.Version9P2000L, cancellationToken: Ct);
            return (await connecting, server);
        }

        public Call Select(uint command) => Pending.Values.ElementAt((int)((command >> 8) % (uint)Pending.Count));

        public async Task<Call> IssueAsync()
        {
            // Owned immediately by the machine, including when the subsequent wire exchange fails.
#pragma warning disable CA2000
            CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
#pragma warning restore CA2000
            _cancellations.Add(cancel);
            Task<Rclunk> reply = Session.Messages.ClunkAsync(new Tclunk(0, ++_fid), cancel.Token).AsTask();
            Tclunk sent = await Server.ReadAsync<Tclunk>(Ct);
            Assert.Equal(_fid, sent.Fid);
            Assert.NotEqual(Constants.NOTAG, sent.Tag);
            Assert.False(Pending.ContainsKey(sent.Tag), "tag reused while its request was pending");
            Call call = new(sent.Tag, reply, cancel);
            call.Observe();
            All.Add(call);
            Pending.Add(sent.Tag, call);
            return call;
        }

        public async Task CompleteAsync(Call call, bool error = false)
        {
            if (error)
            {
                await Server.WriteAsync(new Rlerror(call.Tag, Errno.EIO), Ct);
                NinePException failure = await Assert.ThrowsAsync<NinePException>(async () => await call.Reply.WaitAsync(Ct));
                Assert.Equal(Errno.EIO, failure.Error.Errno);
            }
            else
            {
                await Server.WriteAsync(new Rclunk(call.Tag), Ct);
                Assert.Equal(call.Tag, (await call.Reply.WaitAsync(Ct)).Tag);
            }
            await call.Observed.WaitAsync(Ct);
            Assert.Equal(1, call.Completions);
            Pending.Remove(call.Tag);
        }

        public async Task CancelAsync(Call call, bool originalWins)
        {
            await call.Cancel.CancelAsync();
            Tflush flush = await Server.ReadAsync<Tflush>(Ct);
            Assert.Equal(call.Tag, flush.OldTag);
            Assert.False(Pending.ContainsKey(flush.Tag));
            Assert.Equal(Pending.Count + 1, Session.Multiplexer.TagsInUse);
            Call witness = await IssueAsync();
            Assert.NotEqual(call.Tag, witness.Tag);
            Assert.NotEqual(flush.Tag, witness.Tag);
            if (originalWins)
            {
                await Server.WriteAsync(new Rclunk(call.Tag), Ct);
            }
            await Server.WriteAsync(new Rflush(flush.Tag), Ct);
            if (originalWins)
            {
                Assert.Equal(call.Tag, (await call.Reply.WaitAsync(Ct)).Tag);
            }
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call.Reply.WaitAsync(Ct));
            }
            Pending.Remove(call.Tag);
            await call.Observed.WaitAsync(Ct);
            Assert.Equal(1, call.Completions);
            // Ordered reply acts as a barrier after Rflush before checking pool occupancy.
            await CompleteAsync(witness);
        }

        public async Task ResetAsync()
        {
            await Server.DisposeAsync();
            foreach (Call call in Pending.Values)
            {
                await Assert.ThrowsAnyAsync<NinePException>(async () => await call.Reply.WaitAsync(Ct));
                await call.Observed.WaitAsync(Ct);
                Assert.Equal(1, call.Completions);
            }
            Pending.Clear();
            await Session.DisposeAsync();
            (Session, Server) = await ConnectAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Session.DisposeAsync();
            foreach (CancellationTokenSource cancel in _cancellations) { cancel.Dispose(); }
        }
    }
}
