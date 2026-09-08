using NineP.Protocol.Transports;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>The address grammar of §5.5: six schemes, and nothing else.</summary>
public sealed class NinePAddressTests
{
    /// <summary>Every legal form parses into the fields the scheme gives it.</summary>
    [Theory]
    [InlineData("tcp://127.0.0.1:564", NinePScheme.Tcp, "127.0.0.1", 564, "")]
    [InlineData("tcp://example.org:1", NinePScheme.Tcp, "example.org", 1, "")]
    [InlineData("tcp://[::1]:65535", NinePScheme.Tcp, "[::1]", 65535, "")]
    [InlineData("tls://example.org:564", NinePScheme.Tls, "example.org", 564, "")]
    [InlineData("ws://localhost:8080", NinePScheme.Ws, "localhost", 8080, "/")]
    [InlineData("ws://localhost:8080/9p", NinePScheme.Ws, "localhost", 8080, "/9p")]
    [InlineData("ws://localhost:8080/a/b", NinePScheme.Ws, "localhost", 8080, "/a/b")]
    [InlineData("wss://localhost:8443/9p", NinePScheme.Wss, "localhost", 8443, "/9p")]
    [InlineData("unix:///tmp/ninep.sock", NinePScheme.Unix, "", 0, "/tmp/ninep.sock")]
    [InlineData("memory://alpha", NinePScheme.Memory, "alpha", 0, "")]
    public void LegalAddressesParse(string text, NinePScheme scheme, string host, int port, string path)
    {
        NinePAddress address = NinePAddress.Parse(text);

        Assert.Equal(scheme, address.Scheme);
        Assert.Equal(host, address.Host);
        Assert.Equal(port, address.Port);
        Assert.Equal(path, address.Path);
    }

    /// <summary>Every legal form renders back to the text it was parsed from.</summary>
    [Theory]
    [InlineData("tcp://127.0.0.1:564")]
    [InlineData("tls://example.org:564")]
    [InlineData("ws://localhost:8080/9p")]
    [InlineData("wss://localhost:8443/9p")]
    [InlineData("unix:///tmp/ninep.sock")]
    [InlineData("memory://alpha")]
    public void CanonicalFormRoundTrips(string text) =>
        Assert.Equal(text, NinePAddress.Parse(text).ToString());

    /// <summary>A path-less WebSocket URL renders with the root path it was given.</summary>
    [Fact]
    public void APathlessWebSocketUrlGainsTheRoot() =>
        Assert.Equal("ws://localhost:8080/", NinePAddress.Parse("ws://localhost:8080").ToString());

    /// <summary>Nothing outside the six schemes and the exact grammar is an address.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("http://localhost:8080")]
    [InlineData("tcp:/127.0.0.1:564")]
    [InlineData("://127.0.0.1:564")]
    [InlineData("tcp://127.0.0.1")]
    [InlineData("tcp://127.0.0.1:")]
    [InlineData("tcp://:564")]
    [InlineData("tcp://127.0.0.1:65536")]
    [InlineData("tcp://127.0.0.1:56a")]
    [InlineData("tcp://127.0.0.1:+564")]
    [InlineData("tcp://127.0.0.1:564/path")]
    [InlineData("tls://127.0.0.1:564/path")]
    [InlineData("ws://127.0.0.1/9p")]
    [InlineData("unix://relative/path")]
    [InlineData("unix:///")]
    [InlineData("memory://")]
    [InlineData("memory://a/b")]
    [InlineData("memory://a:1")]
    [InlineData("TCP://127.0.0.1:564")]
    public void IllegalAddressesAreRefused(string text)
    {
        Assert.False(NinePAddress.TryParse(text, out NinePAddress parsed));
        Assert.Equal(default, parsed);
    }

    /// <summary>Port 0 is legal: it is how a listener asks the kernel to choose one.</summary>
    [Fact]
    public void PortZeroIsTheWildcardListenPort()
    {
        NinePAddress address = NinePAddress.Parse("tcp://127.0.0.1:0");

        Assert.Equal(0, address.Port);
        Assert.Equal("tcp://127.0.0.1:0", address.ToString());
    }

    /// <summary>A host may perfectly well be spelled "host"; only the grammar decides.</summary>
    [Fact]
    public void AHostNamedHostIsStillAHost() =>
        Assert.Equal("host", NinePAddress.Parse("tcp://host:564").Host);

    /// <summary>Parse names the offending text so a misconfiguration is readable.</summary>
    [Fact]
    public void ParseNamesTheOffendingText()
    {
        FormatException failure = Assert.Throws<FormatException>(() => NinePAddress.Parse("smtp://x:25"));

        Assert.Contains("smtp://x:25", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A null input is refused rather than crashing the caller.</summary>
    [Fact]
    public void NullIsNotAnAddress()
    {
        Assert.False(NinePAddress.TryParse(null, out _));
        Assert.Throws<FormatException>(() => NinePAddress.Parse(null!));
    }

    /// <summary>IsSecure is true for exactly the two TLS-bearing schemes.</summary>
    [Theory]
    [InlineData("tcp://h:1", false)]
    [InlineData("tls://h:1", true)]
    [InlineData("ws://h:1", false)]
    [InlineData("wss://h:1", true)]
    [InlineData("unix:///s", false)]
    [InlineData("memory://m", false)]
    public void IsSecureFollowsTheScheme(string text, bool expected) =>
        Assert.Equal(expected, NinePAddress.Parse(text).IsSecure);
}
