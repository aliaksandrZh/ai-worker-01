using System.Collections.Concurrent;

namespace PiAgent.Poller;

/// <summary>In-memory poller run state (§14 metrics + cycle gating): per-repository next-due times,
/// the rate-limit pause, and metric counters. Single Poller instance in V1 — in-memory is correct
/// (cursor durability lives in sync_cursor rows).</summary>
public sealed class PollerState
{
    /// <summary>full_name → earliest next poll (decisions 4/53: 60 s default interval per repository).</summary>
    public ConcurrentDictionary<string, DateTimeOffset> NextDue { get; } = new();

    /// <summary>Rate-limit pause (decision 53): set on 403-remaining-0 / 429, honored until it elapses.</summary>
    public DateTimeOffset? PauseUntil { get; set; }

    /// <summary>Last observed X-RateLimit-Remaining (metrics).</summary>
    public int? RateLimitRemaining { get; set; }

    /// <summary>Consecutive rate-limited cycles — backs the bounded 429 exponential backoff.</summary>
    public int ConsecutiveRateLimited { get; set; }

    private long _errors;

    public long ErrorsTotal => Interlocked.Read(ref _errors);

    public void AddError() => Interlocked.Increment(ref _errors);

    public TimeSpan? LastCycleDuration { get; set; }

    public DateTime? LastSuccessUtc { get; set; }
}
