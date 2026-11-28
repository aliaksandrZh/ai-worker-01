namespace PiAgent.Events;

/// <summary>A config-gated worker loop that processes its event types from the log.</summary>
public interface IEventConsumer
{
    /// <summary>Event payload types this consumer handles.</summary>
    Type[] Consumes { get; }

    /// <summary>The Workers:{Name}:Enabled key.</summary>
    string Name { get; }

    Task HandleAsync(IEvent evt, int attempt, long causationId, CancellationToken ct);
}

/// <summary>
/// Base for consumers: declare the payload type, handle events. The worker loop,
/// claiming, redelivery and dead-lettering are the EventLog module's business
/// (docs/eventlog.md).
/// </summary>
public abstract class EventConsumer<TEvent> : IEventConsumer where TEvent : IEvent
{
    public Type[] Consumes { get; } = [typeof(TEvent)];

    public string Name => GetType().Name;

    Task IEventConsumer.HandleAsync(IEvent evt, int attempt, long causationId, CancellationToken ct) =>
        evt is TEvent typed
            ? HandleAsync(typed, attempt, causationId, ct)
            : throw new InvalidOperationException(
                $"consumer {Name} received {evt.GetType().Name}, expected {typeof(TEvent).Name}");

    /// <summary>
    /// attempt ≥ 1; attempt &gt; 1 means a previous delivery did not finish —
    /// the handler may soften work, not skip it. causationId is the claimed log row's id —
    /// the causation for the events this handler appends (docs/event-correlation.md).
    /// </summary>
    protected abstract Task HandleAsync(TEvent evt, int attempt, long causationId, CancellationToken ct);
}
