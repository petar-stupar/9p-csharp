using System.Globalization;
using NineP.Protocol;

namespace NineP.Conformance;

internal static partial class Scenario
{
    private static readonly string[] MissingPaths = new[] { "/missing", "/dir/missing", "/missing/deeper" };
    private static readonly string[] DirectoryAliases = new[] { "/dir/", "//dir", "/dir/../dir" };
    private static readonly string[] ExistingPaths = new[] { "/dir", "/name", "/emptyobj" };
    private static readonly string[] EncodedNames = new[] { "%", "%25", "%2F", "%2E", "%2E%2E", "%252F" };
    private static readonly string[] RenamedEncodedNames = new[] { "%", "%25", "slash", "%2E", "%2E%2E", "%252F" };
    private static readonly string[][] RenameCollisions = new[] { new[] { "/name", "/enabled" }, ["/name", "/dir"], ["/dir", "/name"], ["/dir", "/emptyobj"] };

    internal static async Task<IReadOnlyList<ConformanceOutcome>> PartEAsync(ConformanceTarget target, bool writable)
    {
        List<ConformanceOutcome> outcomes = [];
        async Task Check(string id, Func<Task> body)
        {
            try
            {
                await body();
                outcomes.Add(new(target.Name, id, true, string.Empty));
            }
            catch (ConformanceFailure failure)
            {
                outcomes.Add(new(target.Name, id, false, failure.Message));
            }
        }
        async Task Ok(params string[] args) => Require(await target.RunAsync(args), 0, string.Join(' ', args));
        async Task Text(string expected, params string[] args) => RequireText(await target.RunAsync(args), expected, string.Join(' ', args));
        async Task Error(int errno, params string[] args) => RequireError(await target.RunAsync(args), errno, string.Join(' ', args));
        async Task Write(string path, string text)
        {
            byte[] bytes = ConformanceText.Utf8.GetBytes(text);
            RequireText(await target.RunAsync(["write", path], bytes), $"wrote {bytes.Length}\n", "write " + path);
        }
        async Task<string> Output(params string[] args)
        {
            CliResult result = await target.RunAsync(args);
            Require(result, 0, string.Join(' ', args));
            return result.Text;
        }
        static void Contains(string text, string expected)
        {
            if (!text.Contains(expected, StringComparison.Ordinal))
            {
                throw new ConformanceFailure("missing " + expected + " in " + text);
            }
        }

        if (!writable)
        {
            await Check("E1", async () =>
            {
                foreach (string path in MissingPaths)
                {
                    await Error(Errno.ENOENT, "ls", path);
                }

                await Error(Errno.ENOENT, "stat", "/missing");
                await Error(Errno.ENOENT, "cat", "/dir/sub/missing");
            });
            await Check("E2", async () =>
            {
                await Text("", "ls", "/emptyobj");
                await Text("", "ls", "/emptyarr");
            });
            await Check("E3", async () =>
            {
                await Error(Errno.ENOTDIR, "ls", "/name/x");
                await Error(Errno.ENOTDIR, "cat", "/name/x");
                await Error(Errno.ENOTDIR, "stat", "/dir/file.txt/x");
            });
            await Check("E4", async () =>
            {
                string directory = await Output("ls", "/dir");
                foreach (string alias in DirectoryAliases)
                {
                    await Text(directory, "ls", alias);
                }

                await Text(await Output("ls", "/"), "ls", "/../..");
                Contains(await Output("stat", "/"), "kind=dir");
            });
            await Check("E5", async () =>
            {
                string longest = new('世', 85);
                await Text(".hidden\nhéllo wörld\n" + longest + "\nＡ\n🚀\n", "ls", "/names");
                await Text("astral", "cat", "/names/🚀");
                Contains(await Output("stat", "/names/" + longest), "kind=file size=9 ");
            });
            await Check("E6", async () =>
            {
                foreach (string name in new[] { new string('x', 256), new string('é', 128) })
                {
                    Require(await target.RunAsync(["mkdir", "/" + name]), 1, "overlong mkdir");
                }
            });
            await Check("E14", async () =>
            {
                string root = await Output("ls", "/");
                string name = await Output("cat", "/name");
                await Error(Errno.EROFS, "mkdir", "/readonly-new");
                RequireError(await target.RunAsync(["write", "/readonly-new"], "x"u8.ToArray()), Errno.EROFS, "create read-only");
                RequireError(await target.RunAsync(["write", "/name"], []), Errno.EROFS, "truncate read-only");
                await Error(Errno.EROFS, "rm", "/name");
                await Error(Errno.EROFS, "mv", "/name", "/renamed");
                await Text(root, "ls", "/");
                await Text(name, "cat", "/name");
            });
            return outcomes;
        }

        await Check("E7", async () =>
        {
            foreach (string path in ExistingPaths)
            {
                await Error(Errno.EEXIST, "mkdir", path);
            }

            RequireError(await target.RunAsync(["write", "/dir"], "x"u8.ToArray()), Errno.EISDIR, "write directory");
        });
        await Check("E8", async () =>
        {
            await Error(Errno.ENOENT, "mkdir", "/nope/x");
            RequireError(await target.RunAsync(["write", "/nope/x"], "x"u8.ToArray()), Errno.ENOENT, "write missing parent");
            await Error(target.Dialect == Dialect.P9_2000_L ? Errno.ENOENT : Errno.EOPNOTSUPP, "mv", "/name", "/nope/x");
        });
        await Check("E9", async () =>
        {
            await Error(Errno.ENOENT, "rm", "/missing");
            int denied = target.Dialect == Dialect.P9_2000 ? Errno.EACCES : Errno.EPERM;
            await Error(denied, "rm", "/");
            await Error(denied, "rm", "/dir/..");
            Contains(await Output("ls", "/"), "dir/\n");
        });
        await Check("E10", async () =>
        {
            await Error(Errno.EEXIST, "mv", "/name", "/enabled");
            await Error(Errno.ENOENT, "mv", "/missing", "/new");
            await Error(target.Dialect == Dialect.P9_2000_L ? Errno.EINVAL : Errno.EOPNOTSUPP, "mv", "/dir", "/dir/sub/moved");
            await Ok("mv", "/name", "/name");
            await Ok("mv", "/dir", "/dir");
        });
        await Check("E11", async () =>
        {
            await Write("/name", "");
            await Text("", "cat", "/name");
            Contains(await Output("stat", "/name"), "size=0 ");
        });
        await Check("E12", async () =>
        {
            await Ok("mkdir", "/gone");
            await Ok("rm", "/gone");
            await Error(Errno.ENOENT, "ls", "/gone");
            await Ok("mkdir", "/gone");
            await Ok("rm", "/gone");
        });
        await Check("E13", async () =>
        {
            string root = await Output("ls", "/");
            await Ok("mkdir", "/e2");
            await Text("", "ls", "/e2");
            await Ok("rm", "/e2");
            await Text(root, "ls", "/");
        });
        await Check("E15", async () =>
        {
            await Write("/name", "longer original value");
            await Write("/name", "é");
            await Text("é", "cat", "/name");
            Contains(await Output("stat", "/name"), "size=2 ");
        });
        await Check("E16", async () =>
        {
            int count = (await Output("ls", "/list")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            string last = "/list/" + (count - 1).ToString(CultureInfo.InvariantCulture);
            await Ok("rm", last);
            await Write(last, "last");
            await Error(Errno.EINVAL, "rm", "/list/0");
            string listing = await Output("ls", "/list");
            foreach (string index in new[] { "-1", "01", (count + 1).ToString(CultureInfo.InvariantCulture) })
            {
                RequireError(await target.RunAsync(["write", "/list/" + index], "x"u8.ToArray()), Errno.EINVAL, "array index " + index);
            }

            await Text(listing, "ls", "/list");
            await Text("zero", "cat", "/list/0");
            await Text("last", "cat", last);
        });
        await Check("E17", async () =>
        {
            await Ok("mkdir", "/encoded");
            foreach (string name in EncodedNames)
            {
                await Write("/encoded/" + name, name);
                await Text(name, "cat", "/encoded/" + name);
            }
            await Ok("mv", "/encoded/%2F", "/encoded/slash");
            await Text("%2F", "cat", "/encoded/slash");
            await Text("%252F", "cat", "/encoded/%252F");
            foreach (string name in RenamedEncodedNames)
            {
                await Ok("rm", "/encoded/" + name);
            }

            await Ok("rm", "/encoded");
        });
        await Check("E18", async () =>
        {
            string root = await Output("ls", "/");
            string name = await Output("cat", "/name");
            string enabled = await Output("cat", "/enabled");
            string child = await Output("cat", "/dir/sub/deep/leaf");
            foreach (string[] pair in RenameCollisions)
            {
                await Error(Errno.EEXIST, "mv", pair[0], pair[1]);
            }

            await Text(root, "ls", "/");
            await Text(name, "cat", "/name");
            await Text(enabled, "cat", "/enabled");
            await Text(child, "cat", "/dir/sub/deep/leaf");
        });
        await Check("E19", async () =>
        {
            static string Qid(string stat) => stat.Split("qid=", StringSplitOptions.None)[1].Trim();
            static string Path(string stat) => Qid(stat).Split('.')[^1];
            await Write("/identity", "one");
            string before = await Output("stat", "/identity");
            await Write("/identity", "two");
            string after = await Output("stat", "/identity");
            if (Path(before) != Path(after) || Qid(before) == Qid(after))
            {
                throw new ConformanceFailure("write qid identity/version");
            }

            await Ok("mv", "/identity", "/identity-renamed");
            if (Path(after) != Path(await Output("stat", "/identity-renamed")))
            {
                throw new ConformanceFailure("rename changed identity");
            }

            await Ok("rm", "/identity-renamed");
            await Write("/identity-renamed", "new");
            if (Path(after) == Path(await Output("stat", "/identity-renamed")))
            {
                throw new ConformanceFailure("recreate reused identity");
            }

            await Ok("rm", "/identity-renamed");
        });
        return outcomes;
    }
}
