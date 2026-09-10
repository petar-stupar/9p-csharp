using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Internal;
using NineP.Protocol.Transports.Internal;
using NineP.Server;
using Xunit;

namespace NineP.Repo.Tests;

/// <summary>Runs a git command in the repository and returns its standard output.</summary>
internal static class Git
{
    /// <summary>Runs <c>git</c> with the given arguments; throws when git is unavailable or fails.</summary>
    public static string Run(params string[] arguments)
    {
        ProcessStartInfo info = new("git")
        {
            WorkingDirectory = RepoLayout.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("git did not start");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return process.ExitCode == 0
            ? output
            : throw new InvalidOperationException(
                string.Format(CultureInfo.InvariantCulture, "git {0} failed: {1}", string.Join(' ', arguments), error));
    }

    /// <summary>Every file git tracks, as repository-relative paths.</summary>
    public static IReadOnlyList<string> TrackedFiles() =>
        Run("ls-files").Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>Reads the C# sources of a layer without their comments.</summary>
internal static class Sources
{
    private static readonly Regex LineComment = new(@"//.*$", RegexOptions.Multiline);

    /// <summary>Every <c>.cs</c> file under the given repository-relative directories.</summary>
    public static IEnumerable<string> Under(params string[] directories)
    {
        foreach (string directory in directories)
        {
            string root = RepoLayout.Path(directory);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (!file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                    !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>The file's text with line comments removed, so prose cannot trip a code check.</summary>
    public static string CodeOf(string file) => LineComment.Replace(File.ReadAllText(file), string.Empty);
}

/// <summary>Repository-wide invariants that no single package owns.</summary>
public sealed class RepoHygieneTests
{
    private static readonly Regex InternalType = new(
        @"^internal[A-Za-z ]*\b(?:class|struct|interface|enum|record)\s+(?<name>[A-Za-z0-9_]+)",
        RegexOptions.Multiline);

    private static readonly Regex TopLevelType = new(
        @"^(?:public|internal|file|sealed|abstract|static|partial|readonly|unsafe|ref|record|new)"
        + @"[A-Za-z ]*\b(?:class|struct|interface|enum|record)\s+(?<name>[A-Za-z0-9_]+)",
        RegexOptions.Multiline);

    private const string WireVectorsSha256 =
        "69740D78CA08CFEAFE850889D7D2D04A1DEC76D3A36D8913FA8C15A4A8D15640";

    private static readonly string[] BannedInProductionCode =
    [
        "BitConverter",
        "Encoding.UTF8",
        "DateTime.UtcNow",
        "DateTime.Now",
        "Process.PeakWorkingSet64",
    ];

    private static readonly string[] SqlVerbs =
    [
        "SELECT ", "INSERT INTO", "DELETE FROM", "UPDATE ", "CREATE TABLE", "DROP TABLE",
    ];

    /// <summary>Every type §5.9 declares internal, which no public API file may name.</summary>
    private static readonly string[] InternalTypeNames =
    [
        "ServerSession", "FidTable", "FidEntry", "TagTable", "Dispatcher", "AttrProjector",
        "DirectoryPacker", "PermissionChecker", "AuthFileHandler", "ListenerContext",
        "TagMultiplexer", "PendingRequest", "TagPool", "ChunkedTransfer",
        "WireReader", "WireWriter", "MessageDecoder", "MessageWriter", "FrameReader",
        "FrameWriter", "FrameLease", "StatCodec", "DirEntryCodec", "NinePText",
        "DialectLegality", "WebSocketHandshake", "RawWebSocketListener",
    ];

    /// <summary>
    /// No tracked text file carries a control byte other than tab and newline. The package icon
    /// is the one binary format tracked, declared <c>binary</c> in <c>.gitattributes</c>, and is
    /// the one extension this walk skips.
    /// </summary>
    [Fact]
    public void NoTrackedFileHasControlBytes()
    {
        List<string> offenders = [];

        foreach (string relative in Git.TrackedFiles())
        {
            string path = RepoLayout.Path(relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || relative.EndsWith(".png", StringComparison.Ordinal))
            {
                continue;
            }

            byte[] bytes = File.ReadAllBytes(path);
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                if ((b < 0x20 && b is not 0x09 and not 0x0A) || b == 0x7F)
                {
                    offenders.Add(string.Format(
                        CultureInfo.InvariantCulture, "{0}: byte 0x{1:x2} at offset {2}", relative, b, i));
                    break;
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>The vendored reference and fixtures are byte-identical to the workspace copy.</summary>
    [Fact]
    public void VendoredDocsAreUnmodified()
    {
        string canonical = Path.GetFullPath(RepoLayout.Path("..", "docs", "9p"));
        if (Directory.Exists(canonical))
        {
            string vendored = RepoLayout.Path("docs", "9p");
            foreach (string file in Directory.GetFiles(vendored, "*", SearchOption.AllDirectories))
            {
                string source = Path.Combine(canonical, Path.GetRelativePath(vendored, file));
                Assert.True(File.Exists(source), $"No canonical source for {file}");
                Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(file));
            }
        }
        else
        {
            // A standalone checkout has no workspace source; its checked-in snapshot is the authority.
            Assert.Equal(string.Empty, Git.Run("status", "--porcelain", "docs/9p").Trim());
        }

        byte[] hash = SHA256.HashData(File.ReadAllBytes(RepoLayout.Path("docs", "9p", "fixtures", "wire-vectors.json")));
        Assert.Equal(WireVectorsSha256, Convert.ToHexString(hash));
    }

    /// <summary>Production code names none of the banned host-endian, lossy or wall-clock APIs.</summary>
    [Fact]
    public void ProductionCodeNamesNoBannedApi()
    {
        List<string> offenders = [];

        foreach (string file in Sources.Under("src", "examples"))
        {
            string code = Sources.CodeOf(file);
            foreach (string banned in BannedInProductionCode)
            {
                if (code.Contains(banned, StringComparison.Ordinal))
                {
                    offenders.Add($"{Relative(file)}: {banned}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>SQL lives only in the todofs storage module (workspace architecture §8 item 7).</summary>
    [Fact]
    public void SqlIsConfinedToTheTodoFsStore()
    {
        string allowed = RepoLayout.Path("examples", "NineP.TodoFs", "Storage") + Path.DirectorySeparatorChar;
        List<string> offenders = [];

        foreach (string file in Sources.Under("src", "examples"))
        {
            if (file.StartsWith(allowed, StringComparison.Ordinal))
            {
                continue;
            }

            string code = Sources.CodeOf(file);
            foreach (string verb in SqlVerbs)
            {
                if (code.Contains(verb, StringComparison.Ordinal))
                {
                    offenders.Add($"{Relative(file)}: {verb.Trim()}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>No type §5.9 declares internal appears in a package's public API file.</summary>
    [Fact]
    public void NoInternalTypeIsPublic()
    {
        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(RepoLayout.Path("src"), "PublicAPI.*.txt", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(file);
            foreach (string name in InternalTypeNames)
            {
                foreach (string line in lines)
                {
                    if (DeclaresType(line, name))
                    {
                        offenders.Add($"{Relative(file)}: {name}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>Every public API baseline file starts with the nullable directive RS0037 needs.</summary>
    [Fact]
    public void PublicApiFilesEnableNullableTracking()
    {
        string[] files = [.. Directory.EnumerateFiles(RepoLayout.Path("src"), "PublicAPI.*.txt", SearchOption.AllDirectories)];
        Assert.Equal(6, files.Length);

        foreach (string file in files)
        {
            Assert.Equal("#nullable enable", File.ReadLines(file).First());
        }
    }

    /// <summary>
    /// AC-csharp-3 and exit criterion 4 (RK-78): <c>docs/interop.md</c> states what was run against
    /// which peer, and every peer row's verdict is <c>pass</c>, <c>fail</c> or
    /// <c>not run: &lt;reason&gt;</c>. "Not run" is a legitimate answer and a blank is not: interop
    /// that was not run is not interop that passed (loop lesson 9).
    /// </summary>
    [Fact]
    public void InteropDocListsMergedLanguagesAndPeers()
    {
        string document = File.ReadAllText(RepoLayout.Path("docs", "interop.md"));

        Assert.Contains("none yet", document, StringComparison.Ordinal);
        Assert.Contains("p9ufs", document, StringComparison.Ordinal);
        Assert.Contains("plan9port", document, StringComparison.Ordinal);
        Assert.Contains("Tfsync", document, StringComparison.Ordinal);

        List<string> verdicts = InteropVerdicts(document);
        Assert.True(verdicts.Count >= 4, $"docs/interop.md has {verdicts.Count} peer rows");

        foreach (string verdict in verdicts)
        {
            Assert.True(
                verdict is "pass" or "fail" || verdict.StartsWith("not run: ", StringComparison.Ordinal),
                $"\"{verdict}\" is not pass, fail or \"not run: <reason>\"");
        }
    }

    /// <summary>The result cell of every row of the peer table in <c>docs/interop.md</c>.</summary>
    /// <param name="document">The whole document.</param>
    /// <returns>The verdicts, stripped of emphasis and code markers.</returns>
    private static List<string> InteropVerdicts(string document)
    {
        List<string> verdicts = [];
        bool inTable = false;

        foreach (string line in document.Split('\n'))
        {
            if (line.StartsWith("| peer |", StringComparison.Ordinal))
            {
                inTable = true;
                continue;
            }

            if (!inTable)
            {
                continue;
            }

            if (!line.StartsWith("| ", StringComparison.Ordinal))
            {
                break;
            }

            string[] cells = line.Trim().Trim('|').Split('|');
            if (cells.Length < 6 || cells[0].Trim().StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            verdicts.Add(cells[5].Trim().Trim('*').Trim('`').Trim());
        }

        return verdicts;
    }


    // ---------------------------------------------------------------------------------------
    // D-2, task 45: docs/api.md is the description of the public surface the owner reviews and
    // thirteen languages mirror, so it is checked against that surface rather than trusted. The
    // three assemblies below are the ones dotnet pack packs — the same src/* outputs the nupkgs
    // carry in lib/ — so reflecting over them is reflecting over what ships.
    // ---------------------------------------------------------------------------------------
    private static readonly Assembly[] Packed =
    [
        typeof(Qid).Assembly,
        typeof(NinePSession).Assembly,
        typeof(NinePServer).Assembly,
    ];

    /// <summary>
    /// Members every record gets from the compiler, and the accessor methods behind properties.
    /// <c>docs/api.md</c> states the record boilerplate once, in its preamble, rather than
    /// repeating seven members under each of eighty records; spelling them out would add some six
    /// hundred lines of noise to a document whose whole point is that it can be read front to back.
    /// </summary>
    private static readonly HashSet<string> Compiler = new(StringComparer.Ordinal)
    {
        "Equals",
        "GetHashCode",
        "ToString",
        "op_Equality",
        "op_Inequality",
        "Deconstruct",
        "PrintMembers",
        "EqualityContract",
        "<Clone>$",
        "Finalize",
        "MemberwiseClone",
        "GetType",
        "ReferenceEquals",
    };


    /// <summary>Every public type and member is described, and nothing else is named as one.</summary>
    [Fact]
    public void ApiDocMatchesPublicSurface()
    {
        string document = Document();
        List<string> missing = [];

        foreach (Assembly assembly in Packed)
        {
            foreach (Type type in assembly.GetExportedTypes())
            {
                string name = Simple(type);
                if (!document.Contains(name, StringComparison.Ordinal))
                {
                    missing.Add($"{assembly.GetName().Name}: type {type.FullName} is not in docs/api.md");
                    continue;
                }

                foreach (string member in Members(type))
                {
                    if (!document.Contains(member, StringComparison.Ordinal))
                    {
                        missing.Add($"{type.FullName}.{member} is not in docs/api.md");
                    }
                }
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));

        // The other direction: every type section names a type that really is public.
        HashSet<string> exported = [.. Packed.SelectMany(a => a.GetExportedTypes()).Select(Simple)];
        List<string> invented = [];

        foreach (string heading in TypeSections(document))
        {
            if (!exported.Contains(heading))
            {
                invented.Add($"docs/api.md has a section for \"{heading}\", which is not a public type");
            }
        }

        Assert.True(invented.Count == 0, string.Join(Environment.NewLine, invented));
    }

    /// <summary>
    /// §5.10's inventory, exactly: 133 public types in <c>NineP.Protocol</c>, 5 in
    /// <c>NineP.Client</c>, 17 in <c>NineP.Server</c>. A type added or removed without updating
    /// the table in <c>docs/api.md</c> and the spec fails here.
    /// </summary>
    [Theory]
    [InlineData("NineP.Protocol", 133)]
    [InlineData("NineP.Client", 5)]
    [InlineData("NineP.Server", 17)]
    public void PublicTypeCountMatchesSpec(string assembly, int expected)
    {
        Assembly packed = Packed.Single(a => a.GetName().Name == assembly);
        Type[] exported = packed.GetExportedTypes();

        Assert.Equal(expected, exported.Length);
    }

    /// <summary>
    /// The surface is frozen at 0.1.0, as reviewed by the owner on 2026-09-08: every line of every
    /// <c>PublicAPI.Unshipped.txt</c> has moved into the matching <c>PublicAPI.Shipped.txt</c>, and
    /// the shipped files together list exactly the types the assemblies export. RS0016 and RS0017
    /// are build errors, so the files cannot fall behind the compiler; this is what says they have
    /// been promoted rather than merely kept up to date.
    /// </summary>
    [Fact]
    public void PublicApiFileIsUpToDate()
    {
        HashSet<string> shipped = new(StringComparer.Ordinal);

        foreach (Assembly assembly in Packed)
        {
            string project = RepoLayout.Path("src", assembly.GetName().Name!);

            string[] unshipped = Entries(Path.Combine(project, "PublicAPI.Unshipped.txt"));
            Assert.True(
                unshipped.Length == 0,
                $"{assembly.GetName().Name}/PublicAPI.Unshipped.txt still holds {unshipped.Length} entries");

            foreach (string entry in Entries(Path.Combine(project, "PublicAPI.Shipped.txt")))
            {
                shipped.Add(entry);
            }
        }

        List<string> absent = [];
        foreach (Type type in Packed.SelectMany(assembly => assembly.GetExportedTypes()))
        {
            if (!shipped.Contains(type.FullName!.Replace('+', '.')))
            {
                absent.Add(type.FullName + " is not in any PublicAPI.Shipped.txt");
            }
        }

        Assert.True(absent.Count == 0, string.Join(Environment.NewLine, absent));
    }

    /// <summary>The names of the public members of a type that a reader would look for.</summary>
    private static IEnumerable<string> Members(Type type)
    {
        const BindingFlags Public =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (MemberInfo member in type.GetMembers(Public))
        {
            if (member is MethodInfo { IsSpecialName: true } or ConstructorInfo || member is Type)
            {
                continue;
            }

            // value__ is the backing field the compiler gives every enum; it is not API.
            if (Compiler.Contains(member.Name) || member.Name.StartsWith('<') || member.Name == "value__")
            {
                continue;
            }

            yield return member.Name;
        }
    }

    /// <summary>A type's name as the document writes it, without arity or namespace.</summary>
    private static string Simple(Type type)
    {
        string name = type.Name;
        int arity = name.IndexOf('`', StringComparison.Ordinal);

        return arity < 0 ? name : name[..arity];
    }

    /// <summary>
    /// The type named by every <c>### </c> section that claims to be one. A heading whose whole
    /// text is a single backticked identifier is a type section and must name a public type;
    /// anything else — "Record boilerplate", "The handler model", the four grouped message-record
    /// tables — is prose about the API rather than a claim that a type exists.
    /// </summary>
    /// <param name="document">The whole document.</param>
    /// <returns>The type names the document declares sections for.</returns>
    private static IEnumerable<string> TypeSections(string document)
    {
        foreach (string line in document.Split('\n'))
        {
            if (!line.StartsWith("### ", StringComparison.Ordinal))
            {
                continue;
            }

            string heading = line[4..].Trim();
            if (heading.Length > 2 && heading[0] == '`' && heading[^1] == '`'
                && heading.AsSpan(1, heading.Length - 2).IndexOf('`') < 0
                && heading.AsSpan(1, heading.Length - 2).IndexOf(' ') < 0)
            {
                yield return heading[1..^1];
            }
        }
    }

    private static string[] Entries(string path) =>
        [.. File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && line != "#nullable enable")];

    private static string Document() =>
        File.ReadAllText(RepoLayout.Path("docs", "api.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    private static bool DeclaresType(string line, string typeName)
    {
        int marker = line.IndexOf(typeName, StringComparison.Ordinal);
        if (marker < 0)
        {
            return false;
        }

        int after = marker + typeName.Length;
        char following = after < line.Length ? line[after] : ' ';
        char preceding = marker > 0 ? line[marker - 1] : '.';

        return (preceding is '.' or ' ') && following is ' ' or '.' or '\0' or '<';
    }

    private static string Relative(string path) =>
        path[(RepoLayout.Root.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// IR-2: under <c>src/</c> and <c>examples/</c>, one file declares exactly one top-level type
    /// and is named after it. Nested types are unaffected — a private implementation detail stays
    /// inside its containing type. Two deliberate exceptions: <c>Program.cs</c>, whose top-level
    /// statements compile into a type the language will not let anyone name, and the
    /// <c>Type.Aspect.cs</c> form for one half of a partial, where the name up to the first dot is
    /// what must match. <c>tests/</c> is exempt by directory and deliberately so (IR-2 point 1): a
    /// test class with one fake alongside it is idiomatic xUnit, and splitting those would satisfy
    /// a convention that exists for navigability in production code. Do not "fix" that by adding
    /// tests/ here.
    /// </summary>
    [Fact]
    public void EverySourceFileHoldsOneTypeNamedAfterIt()
    {
        List<string> offenders = [];

        foreach (string file in Sources.Under("src", "examples"))
        {
            string name = Path.GetFileName(file);
            if (name == "Program.cs")
            {
                continue;
            }

            string[] declared = TopLevelTypes(File.ReadAllText(file));
            string expected = name[..name.IndexOf('.', StringComparison.Ordinal)];

            if (declared.Length != 1)
            {
                offenders.Add($"{name} declares {declared.Length} top-level types: {string.Join(", ", declared)}");
            }
            else if (declared[0] != expected)
            {
                offenders.Add($"{name} declares {declared[0]}, not {expected}");
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The names of the types a file declares at namespace level. A declaration starts in column 1
    /// with its modifiers, which is what separates it from a nested type; XML doc comments and
    /// attributes never do, so neither can be mistaken for one.
    /// </summary>
    /// <param name="source">The file's text.</param>
    /// <returns>The declared names, in the order they appear.</returns>
    private static string[] TopLevelTypes(string source) =>
        [.. TopLevelType.Matches(source).Select(match => match.Groups["name"].Value)];


    /// <summary>
    /// IR-3: in the published packages, a directory holds surface or implementation, never both.
    /// Every type declared <c>internal</c> lives under an <c>Internal/</c> directory, and its
    /// namespace follows the directory as every namespace in this repository does. Splitting one
    /// type per file (IR-2) is what made this visible: types like <c>TlsListener</c> and
    /// <c>WireReader</c> used to be nested inside the public type's file and are now files of
    /// their own, so without this rule they sit beside the API a consumer is meant to read.
    /// </summary>
    [Fact]
    public void ImplementationTypesLiveUnderAnInternalDirectory()
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        List<string> misplaced = [];

        foreach (string file in Sources.Under("src"))
        {
            if (file.Contains($"{separator}Internal{separator}", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match declaration in InternalType.Matches(File.ReadAllText(file)))
            {
                misplaced.Add(
                    $"{Path.GetFileName(file)} declares internal {declaration.Groups["name"].Value} " +
                    "outside an Internal/ directory");
            }
        }

        Assert.True(misplaced.Count == 0, string.Join(Environment.NewLine, misplaced));
    }

}
