using System.Buffers;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>
/// A frame lease owns its bytes for exactly as long as the call that reads them. Reading a lease
/// after it has been returned is loud, not subtle: the property throws, and a debug build clears
/// the rental so that stale bytes cannot masquerade as the next frame (RK-65).
/// </summary>
public sealed class BufferLeaseTests
{
    private static readonly byte[] Payload = [1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>A poisoned rental goes back to the pool full of zeroes.</summary>
    [Fact]
    public void PayloadIsPoisonedAfterDispose()
    {
        // A private pool with one array per bucket hands the same array back on the next rent,
        // which is what makes the poisoning observable rather than merely intended.
        ArrayPool<byte> pool = ArrayPool<byte>.Create(64, 1);
        FrameLease lease = FrameLease.Copied(pool, new ReadOnlySequence<byte>(Payload), poison: true);

        Assert.True(lease.IsPooled);
        Assert.Equal(Payload, lease.Frame.ToArray());

        lease.Dispose();

        byte[] again = pool.Rent(Payload.Length);
        Assert.All(again[..Payload.Length], b => Assert.Equal(0, b));
    }

    /// <summary>Reading a lease after it has been returned throws rather than returning stale bytes.</summary>
    [Fact]
    public void ReadingAfterDisposeThrows()
    {
        ArrayPool<byte> pool = ArrayPool<byte>.Create(64, 1);
        FrameLease lease = FrameLease.Copied(pool, new ReadOnlySequence<byte>(Payload), poison: true);
        lease.Dispose();

        Assert.Throws<ObjectDisposedException>(() => lease.Frame);
    }

    /// <summary>Disposing twice is harmless: the rental goes back exactly once.</summary>
    [Fact]
    public void DisposeIsIdempotent()
    {
        ArrayPool<byte> pool = ArrayPool<byte>.Create(64, 1);
        FrameLease lease = FrameLease.Copied(pool, new ReadOnlySequence<byte>(Payload));

        lease.Dispose();
        lease.Dispose();

        byte[] first = pool.Rent(Payload.Length);
        byte[] second = pool.Rent(Payload.Length);
        Assert.NotSame(first, second);
    }

    /// <summary>Without poisoning the rental keeps its bytes, which is the release-build cost.</summary>
    [Fact]
    public void WithoutPoisoningTheRentalIsNotCleared()
    {
        ArrayPool<byte> pool = ArrayPool<byte>.Create(64, 1);
        FrameLease lease = FrameLease.Copied(pool, new ReadOnlySequence<byte>(Payload), poison: false);
        lease.Dispose();

        byte[] again = pool.Rent(Payload.Length);
        Assert.Equal(Payload, again[..Payload.Length]);
    }

    /// <summary>Poisoning is the default in a debug build and is off in a release build.</summary>
    [Fact]
    public void PoisoningIsTheDebugDefault()
    {
#if DEBUG
        Assert.True(FrameLease.PoisonByDefault);
#else
        Assert.False(FrameLease.PoisonByDefault);
#endif
    }

    /// <summary>A borrowed lease owns nothing and returns nothing.</summary>
    [Fact]
    public void ABorrowedLeaseIsNotPooled()
    {
        using FrameLease lease = FrameLease.Borrowed(Payload);

        Assert.False(lease.IsPooled);
        Assert.Equal(Payload, lease.Frame.ToArray());
    }
}
