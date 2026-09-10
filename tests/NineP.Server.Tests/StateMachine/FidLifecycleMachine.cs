using System.Globalization;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.Protocol.Negotiation;
using NineP.Protocol.Transports;
using NineP.Server.Internal;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.StateMachine;

[Trait("Category", "StateMachine")]
public sealed class FidLifecycleMachine
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);
    public static TheoryData<Dialect, uint> Seeds => new()
    {
        { Dialect.P9_2000, 17 }, { Dialect.P9_2000_u, 947 }, { Dialect.P9_2000_L, 65537 },
        { Dialect.P9_2000, 947 }, { Dialect.P9_2000_u, 65537 }, { Dialect.P9_2000_L, 17 },
    };

    [Theory, MemberData(nameof(Seeds))]
    public async Task GeneratedLifecyclesMatchTheIndependentModel(Dialect dialect, uint seed)
    {
        seed = ModelSequences.Seed(seed);
        uint[] prefix = [0, 1, 3 | (2u << 16), 4, 5, 6, 2, 10, 5, 7, 11, 2, 12, 6, 1, 13, 8, 9, 0, 14];
        uint[] commands = ModelSequences.Generate(seed, 15, prefix);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NINEP_MODEL_COMMANDS")))
        {
            Assert.Equal(15, commands.Select(c => c & 255).Distinct().Count());
        }
        await ModelSequences.VerifyAsync(seed, commands, sequence => ExecuteAsync(dialect, sequence), Ct);
    }

    private static async Task ExecuteAsync(Dialect dialect, IReadOnlyList<uint> commands)
    {
        MemoryFilesystem tree = new();
        MemoryFile seed = tree.NewFile("seed", 0x1B6);
        seed.Data = "abc"u8.ToArray();
        tree.Root.Add(seed);
        tree.Root.Add(tree.NewDirectory("sub", 0x1FF));
        Node root = new("/", true);
        Dictionary<string, Node> names = new(StringComparer.Ordinal)
        {
            ["seed"] = new("seed", false) { Data = "abc"u8.ToArray() },
            ["sub"] = new("sub", true),
        };
        Dictionary<uint, Binding> fids = [];
        var (client, server) = MemoryTransport.CreatePair();
        await using FakeNinePServer wire = FakeNinePServer.Wrap(client);
        ServerOptions options = new() { Listen = [new NinePAddress(NinePScheme.Memory, "model", 0, "")] };
        await using ServerSession session = new(server, options, tree, new ServerMetrics(), new OpenState());
        Task running = session.RunAsync(Ct);
        ushort tag = 0;
        await VersionAsync();
        foreach (uint command in commands)
        {
            int operation = (int)(command & 255);
            uint fid = 2 + ((command >> 8) & 255) % 3;
            byte mode = (byte)(((command >> 16) & 255) % 3);
            ulong offset = (command >> 24) % 8;
            fids.TryGetValue(fid, out Binding? held);
            bool attached = fids.ContainsKey(1);
            switch (operation)
            {
                case 0: // Attach also tests duplicate binding; version reset requires re-attach.
                    await ReplyAsync(new Tattach(++tag, 1, Constants.NOFID, "glenda", "", Constants.NONUNAME), !attached);
                    if (!attached) { fids.Add(1, new(root)); }
                    break;
                case 1: // Walk to a shared file, keeping aliases in the model.
                    {
                        bool success = attached && held is null && names.ContainsKey("seed");
                        await ReplyAsync(new Twalk(++tag, 1, fid, ["seed"]), success);
                        if (success) { fids.Add(fid, new(names["seed"])); }
                        break;
                    }
                case 2: // Clone.
                    await ReplyAsync(new Twalk(++tag, 1, fid, []), attached && held is null);
                    if (attached && held is null) { fids.Add(fid, new(root)); }
                    break;
                case 3: // Open once; directories accept read only.
                    {
                        bool success = held is { Mode: null } && (!held.Node.Directory || mode == 0);
                        if (dialect == Dialect.P9_2000_L)
                        {
                            await ReplyAsync(new Tlopen(++tag, fid, mode), success);
                        }
                        else
                        {
                            await ReplyAsync(new Topen(++tag, fid, mode), success);
                        }
                        if (success) { held!.Mode = mode; }
                        break;
                    }
                case 4: // Read including EOF; no handler is consulted for the expected bytes.
                    {
                        bool success = held is { Node.Directory: false, Mode: 0 or 2 };
                        byte[] reply = await ReplyAsync(new Tread(++tag, fid, offset, 2), success);
                        if (success)
                        {
                            byte[] expected = held!.Node.Data.Skip((int)offset).Take(2).ToArray();
                            Assert.Equal(expected, MessageCodec.Decode<Rread>(reply, dialect).Data.ToArray());
                        }
                        break;
                    }
                case 5: // Sparse and mid-file writes to the independent byte-array model.
                    {
                        bool success = held is { Node.Directory: false, Mode: 1 or 2 };
                        byte value = (byte)(command >> 16);
                        byte[] reply = await ReplyAsync(new Twrite(++tag, fid, offset, new byte[] { value }), success);
                        if (success)
                        {
                            Assert.Equal(1u, MessageCodec.Decode<Rwrite>(reply, dialect).Count);
                            byte[] next = new byte[Math.Max(held!.Node.Data.Length, (int)offset + 1)];
                            held.Node.Data.CopyTo(next, 0);
                            next[(int)offset] = value;
                            held.Node.Data = next;
                        }
                        break;
                    }
                case 6:
                    await ReplyAsync(new Tclunk(++tag, fid), held is not null);
                    fids.Remove(fid);
                    break;
                case 7: // Remove frees even a root clone whose removal is refused.
                    {
                        bool success = held is not null && !held.Node.Directory && names.ContainsKey(held.Node.Name);
                        await ReplyAsync(new Tremove(++tag, fid), success);
                        if (success) { names.Remove(held!.Node.Name); }
                        fids.Remove(fid);
                        break;
                    }
                case 8:
                    // Flush the previous, already answered tag: legal, and answered Rflush (reference §5.3).
                    await ReplyAsync(new Tflush(++tag, (ushort)(tag - 1)), true);
                    break;
                case 9:
                    await VersionAsync();
                    fids.Clear();
                    break;
                case 10: // A successful create replaces the directory binding with an open file.
                    {
                        string name = "made" + fid.ToString(CultureInfo.InvariantCulture);
                        bool success = held is { Node.Directory: true, Mode: null } && !names.ContainsKey(name);
                        if (dialect == Dialect.P9_2000_L)
                        {
                            await ReplyAsync(new Tlcreate(++tag, fid, name, 2, 0x1B6, Constants.NONUNAME), success);
                        }
                        else
                        {
                            await ReplyAsync(new Tcreate(++tag, fid, name, 0x1B6, 2, ""), success);
                        }
                        if (success)
                        {
                            Node created = new(name, false);
                            names.Add(name, created);
                            fids[fid] = new(created) { Mode = 2 };
                        }
                        break;
                    }
                case 11: // Partial walk returns qids without binding its newfid.
                    {
                        bool success = attached && held is null;
                        byte[] reply = await ReplyAsync(new Twalk(++tag, 1, fid, ["sub", "missing"]), success);
                        if (success) { Assert.Single(MessageCodec.Decode<Rwalk>(reply, dialect).Wqids); }
                        break;
                    }
                case 12:
                    await ReplyAsync(new Twalk(++tag, fid, fid, []), held is { Mode: null });
                    break;
                case 13: // Even a handler error on clunk retires the number.
                    {
                        MemoryFile? failing = held is { Node.Directory: false } && tree.Root.Children.TryGetValue(held.Node.Name, out MemoryNode? node)
                            ? node as MemoryFile : null;
                        if (failing is not null) { failing.ClunkFailure = NinePError.FromErrno(Errno.EIO); }
                        try
                        {
                            await ReplyAsync(new Tclunk(++tag, fid), held is not null && failing is null);
                        }
                        finally
                        {
                            if (failing is not null) { failing.ClunkFailure = null; }
                        }
                        fids.Remove(fid);
                        break;
                    }
                case 14:
                    await ReplyAsync(new Twalk(++tag, 1, Constants.NOFID, []), false);
                    break;
                default:
                    throw new InvalidOperationException("Unknown model operation");
            }
            Assert.Equal(fids.Count, session.Fids.Count);
            Assert.Equal(names.Keys.Order(StringComparer.Ordinal), tree.Root.Children.Keys.Order(StringComparer.Ordinal));
            foreach ((string name, Node node) in names)
            {
                if (!node.Directory) { Assert.Equal(node.Data, ((MemoryFile)tree.Root.Children[name]).Data); }
            }
        }
        await wire.DisposeAsync();
        await running.WaitAsync(Ct);

        async Task VersionAsync()
        {
            wire.Dialect = Dialect.P9_2000;
            await wire.WriteAsync(new Tversion(Constants.NOTAG, 8192, Negotiator.VersionString(dialect)), Ct);
            Rversion version = await wire.ReadAsync<Rversion>(Ct);
            Assert.Equal(Negotiator.VersionString(dialect), version.Version);
            Assert.Equal(Constants.NOTAG, version.Tag);
            wire.Dialect = dialect;
        }

        async Task<byte[]> ReplyAsync<T>(T request, bool success) where T : struct, IMessage
        {
            await wire.WriteAsync(request, Ct);
            byte[] reply = await wire.ReadFrameAsync(Ct);
            Assert.Equal(tag, MessageCodec.PeekTag(reply));
            MessageType type = MessageCodec.PeekType(reply);
            if (success)
            {
                Assert.Equal((MessageType)((byte)T.Type + 1), type);
            }
            else
            {
                Assert.Equal(dialect == Dialect.P9_2000_L ? MessageType.Rlerror : MessageType.Rerror, type);
                int errno = dialect == Dialect.P9_2000_L ? MessageCodec.Decode<Rlerror>(reply, dialect).Ecode
                    : NinePError.FromEname(MessageCodec.Decode<Rerror>(reply, dialect).Ename).Errno;
                Assert.Contains(errno, new[] { Errno.EBADF, Errno.EINVAL, Errno.EACCES, Errno.EISDIR, Errno.ENOTDIR,
                    Errno.ENOENT, Errno.EEXIST, Errno.EPERM, Errno.EIO, Errno.ERANGE });
            }
            return reply;
        }
    }

    private sealed class Node(string name, bool directory)
    {
        public string Name { get; } = name;
        public bool Directory { get; } = directory;
        public byte[] Data { get; set; } = [];
    }
    private sealed class Binding(Node node)
    {
        public Node Node { get; } = node;
        public byte? Mode { get; set; }
    }
}
