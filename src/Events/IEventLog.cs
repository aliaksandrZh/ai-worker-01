namespace PiAgent.Events;

/// <summary>
/// The producer side of the event log (docs/eventlog.md): append typed events; the
/// transactional append commits the event atomically with the caller's own state change.
/// </summary>
public interface IEventLog
{
    /// <summary>Fire-and-commit append: one INSERT, visible to consumers once the call returns.</summary>
    Task<long> AppendAsync<TEvent>(TEvent evt, CancellationToken ct) where TEvent : IEvent;

    /// <summary>Transactional append: commits atomically with the caller's transaction.</summary>
    Task<long> AppendAsync<TEvent>(TEvent evt, IEventTransaction tx, CancellationToken ct) where TEvent : IEvent;

    /// <summary>Begins a unit of orchestration work the caller commits explicitly. The
    /// causation event id stamps every event appended in the transaction that does not
    /// carry its own (docs/event-correlation.md).</summary>
    Task<IEventTransaction> BeginTransactionAsync(long? causationEventId, CancellationToken ct);
}

/// <summary>A unit of work created inside the module; commit publishes, dispose rolls back.</summary>
public interface IEventTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);

    /// <summary>Id of the event being processed — the default causation for appends in this transaction.</summary>
    long? CausationId { get; }
}
