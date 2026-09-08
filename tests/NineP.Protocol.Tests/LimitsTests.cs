using NineP.Protocol;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>The bounds of the workspace architecture §4 and their shipped defaults.</summary>
public sealed class LimitsTests
{
    /// <summary>The defaults are the ones the architecture fixes, value by value.</summary>
    [Fact]
    public void DefaultsAreTheArchitecturesNumbers()
    {
        Limits limits = Limits.Default;

        Assert.Equal(1024u * 1024u, limits.MaxMsize);
        Assert.Equal(4096u, limits.MinMsize);
        Assert.Equal(8192u, limits.PreNegotiationFrameCap);
        Assert.Equal(65536, limits.MaxFidsPerConnection);
        Assert.Equal(256, limits.MaxInFlightPerConnection);
        Assert.Equal(4096, limits.MaxInFlightPerListener);
        Assert.Equal(8, limits.FlushReservePerConnection);
        Assert.Equal(1024, limits.MaxConnectionsPerListener);
        Assert.Equal(TimeSpan.FromSeconds(30), limits.ReadHeaderTimeout);
        Assert.Equal(TimeSpan.Zero, limits.IdleTimeout);
        Assert.Equal(255, limits.MaxNameLength);
        Assert.Equal(64 * 1024, limits.MaxAuthBytes);
        Assert.Equal(TimeSpan.FromSeconds(30), limits.AuthTimeout);
    }

    /// <summary>
    /// The pre-negotiation cap is a small constant, never the configured maximum: an
    /// unauthenticated peer must not be able to reserve a megabyte per connection by lying in the
    /// size field (reference §8 rule 1).
    /// </summary>
    [Fact]
    public void PreNegotiationCapIsNotTheConfiguredMaximum()
    {
        Assert.Equal(8192u, Limits.Default.PreNegotiationFrameCap);
        Assert.True(Limits.Default.PreNegotiationFrameCap < Limits.Default.MaxMsize);

        Limits raised = Limits.Default with { MaxMsize = 8 * 1024 * 1024 };
        Assert.Equal(8192u, raised.PreNegotiationFrameCap);
    }

    /// <summary>The shipped defaults validate.</summary>
    [Fact]
    public void DefaultsValidate() => Limits.Default.Validate();

    /// <summary>An internally inconsistent record is refused at construction, not under load.</summary>
    [Theory]
    [MemberData(nameof(Inconsistent))]
    public void ValidateRejectsInconsistentLimits(Limits limits) =>
        Assert.Throws<ArgumentOutOfRangeException>(limits.Validate);

    /// <summary>The inconsistent records Validate must refuse.</summary>
    /// <returns>One record per row.</returns>
    public static TheoryData<Limits> Inconsistent() =>
    [
        Limits.Default with { MaxMsize = 1024 },
        Limits.Default with { MinMsize = 0 },
        Limits.Default with { PreNegotiationFrameCap = 4 },
        Limits.Default with { MaxFidsPerConnection = 0 },
        Limits.Default with { MaxInFlightPerListener = 8 },
        Limits.Default with { FlushReservePerConnection = 0 },
        Limits.Default with { FlushReservePerConnection = 256 },
        Limits.Default with { MaxNameLength = 256 },
        Limits.Default with { ReadHeaderTimeout = TimeSpan.FromSeconds(-1) },
    ];
}
