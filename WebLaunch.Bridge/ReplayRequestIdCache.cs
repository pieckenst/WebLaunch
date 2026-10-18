namespace WebLaunch.Bridge;

/// <summary>
/// Keeps one-time launch request IDs for a bounded interval. Expired entries may be evicted;
/// a still-live entry is never evicted merely to make room for a new request.
/// </summary>
internal sealed class ReplayRequestIdCache
{
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> reservations = new(StringComparer.Ordinal);
    private readonly TimeSpan retention;
    private readonly int capacity;
    private readonly TimeProvider timeProvider;

    /// <summary>Creates a replay cache with an injectable clock for deterministic expiry tests.</summary>
    /// <param name="capacity">Maximum number of unexpired IDs retained at once.</param>
    /// <param name="retention">How long an ID remains rejected after it is first reserved.</param>
    /// <param name="timeProvider">Clock used to determine expiry.</param>
    public ReplayRequestIdCache(
        int capacity = 4096,
        TimeSpan? retention = null,
        TimeProvider? timeProvider = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.retention = retention ?? TimeSpan.FromHours(24);
        if (this.retention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retention));
        this.capacity = capacity;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Attempts to reserve a request ID once, atomically with expiry pruning.</summary>
    /// <returns>False for a duplicate ID or when every retained slot is still within its rejection window.</returns>
    public bool TryReserve(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            PruneExpiredUnderLock(now);

            if (reservations.ContainsKey(requestId) || reservations.Count >= capacity)
                return false;

            reservations.Add(requestId, now + retention);
            return true;
        }
    }

    /// <summary>Removes reservations whose rejection windows have elapsed.</summary>
    public void PruneExpired()
    {
        lock (gate)
            PruneExpiredUnderLock(timeProvider.GetUtcNow());
    }

    private void PruneExpiredUnderLock(DateTimeOffset now)
    {
        foreach (var expired in reservations.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
            reservations.Remove(expired);
    }

    internal int Count
    {
        get
        {
            lock (gate)
            {
                PruneExpiredUnderLock(timeProvider.GetUtcNow());
                return reservations.Count;
            }
        }
    }
}
