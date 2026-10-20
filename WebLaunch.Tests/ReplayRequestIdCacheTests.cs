using WebLaunch.Bridge;
using Xunit;

namespace WebLaunch.Tests;

/// <summary>Deterministic tests for replay-window, saturation and concurrency guarantees.</summary>
public sealed class ReplayRequestIdCacheTests
{
    [Fact]
    public void DuplicateRemainsRejectedUntilTheExpiryBoundary()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache = new ReplayRequestIdCache(capacity: 2, retention: TimeSpan.FromHours(24), timeProvider: clock);

        Assert.True(cache.TryReserve("request-1"));
        Assert.False(cache.TryReserve("request-1"));

        clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromTicks(1));
        Assert.False(cache.TryReserve("request-1"));

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(cache.TryReserve("request-1"));
    }

    [Fact]
    public void SaturationNeverEvictsLiveIdsAndRecoversAfterExpiry()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache = new ReplayRequestIdCache(capacity: 2, retention: TimeSpan.FromHours(24), timeProvider: clock);

        Assert.True(cache.TryReserve("first"));
        clock.Advance(TimeSpan.FromHours(12));
        Assert.True(cache.TryReserve("second"));
        Assert.False(cache.TryReserve("third"));
        Assert.False(cache.TryReserve("first"));

        clock.Advance(TimeSpan.FromHours(12));
        Assert.True(cache.TryReserve("third")); // First has expired; second is still protected.
        Assert.False(cache.TryReserve("second"));
        Assert.Equal(2, cache.Count);

        clock.Advance(TimeSpan.FromHours(12));
        Assert.True(cache.TryReserve("second"));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void ConcurrentReservationsForTheSameIdHaveExactlyOneWinner()
    {
        var cache = new ReplayRequestIdCache(capacity: 64);
        var accepted = 0;

        Parallel.For(0, 128, _ =>
        {
            if (cache.TryReserve("same-request"))
                Interlocked.Increment(ref accepted);
        });

        Assert.Equal(1, accepted);
        Assert.Equal(1, cache.Count);
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private readonly object gate = new();
        private DateTimeOffset now = initialTime;

        public override DateTimeOffset GetUtcNow()
        {
            lock (gate) return now;
        }

        public void Advance(TimeSpan elapsed)
        {
            lock (gate) now = now.Add(elapsed);
        }
    }
}
