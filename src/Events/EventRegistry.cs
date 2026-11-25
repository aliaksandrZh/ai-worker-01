using System.Text.Json;

namespace PiAgent.Events;

/// <summary>
/// Maps event type names to payload types. The registry is built once from the Events
/// assembly; implementations use it to (de)serialize envelopes without reflection at
/// claim time beyond one dictionary lookup.
/// </summary>
public static class EventRegistry
{
    private static readonly IReadOnlyDictionary<string, Type> ByName = BuildRegistry();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static IReadOnlyDictionary<string, Type> BuildRegistry()
    {
        var types = typeof(IEvent).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsClass && typeof(IEvent).IsAssignableFrom(t))
            .ToDictionary(t => t.Name, t => t);
        return types;
    }

    public static IReadOnlyDictionary<string, Type> Types => ByName;

    public static string TypeName(Type eventType) => eventType.Name;

    public static string Serialize(IEvent evt) => JsonSerializer.Serialize(evt, evt.GetType(), JsonOptions);

    public static IEvent Deserialize(string typeName, string json) =>
        ByName.TryGetValue(typeName, out var type)
            ? (IEvent)(JsonSerializer.Deserialize(json, type, JsonOptions)
                ?? throw new InvalidOperationException($"event payload deserialized to null: {typeName}"))
            : throw new InvalidOperationException($"unknown event type: {typeName}");
}

/// <summary>
/// A handler declares this failure permanent: no retry can succeed, the event
/// dead-letters immediately (docs/eventlog.md invariant 2).
/// </summary>
public sealed class PermanentDeliveryException(string message) : Exception(message);

/// <summary>The delivery-side view of a claimed event (docs/eventlog.md invariants).</summary>
public sealed record ClaimedEvent(long Id, IEvent Event, int Attempt);

/// <summary>A parked event after repeated failed deliveries (kept + replayable).</summary>
public sealed record DeadLetterEntry(
    long Id,
    string Type,
    string Stream,
    int Attempts,
    string? LastError,
    DateTime DeadAtUtc);

/// <summary>
/// The consumer side of the event log. Delivery semantics (at-least-once, visibility
/// timeout, dead-letter, per-stream order, multi-instance safety) are invariants of the
/// implementations — callers only claim/ack/fail.
/// </summary>
public interface IEventQueue
{
    /// <summary>
    /// Claims up to <paramref name="batch"/> events of the given types: per stream only
    /// the head (no older unprocessed event of that stream) and only when unclaimed.
    /// </summary>
    Task<IReadOnlyList<ClaimedEvent>> ClaimAsync(
        IReadOnlyCollection<string> types, int batch, TimeSpan visibility, CancellationToken ct);

    /// <summary>Marks the event processed.</summary>
    Task AckAsync(long id, CancellationToken ct);

    /// <summary>
    /// Releases the event for redelivery; after <paramref name="maxAttempts"/> failed
    /// deliveries moves it to the dead-letter store.
    /// </summary>
    Task FailAsync(long id, string error, int maxAttempts, CancellationToken ct);

    /// <summary>
    /// Permanent failure declared by a handler: the event moves straight to the
    /// dead-letter store — no retries can succeed (e.g. a gone GitHub entity).
    /// </summary>
    Task PoisonAsync(long id, string error, CancellationToken ct);

    Task<IReadOnlyList<DeadLetterEntry>> ListDeadLettersAsync(int limit, CancellationToken ct);

    /// <summary>Re-enters a parked event into the log with the attempt counter reset.</summary>
    Task<bool> ReplayDeadLetterAsync(long deadLetterId, CancellationToken ct);
}
