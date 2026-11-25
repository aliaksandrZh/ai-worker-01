namespace PiAgent.Events;

/// <summary>A config-gated worker loop that processes its event types from the log.</summary>
public interface IEventConsumer
{
    /// <summary>Event payload types this consumer handles.</summary>
    Type[] Consumes { get; }

    /// <summary>The Workers:{Name}:Enabled key.</summary>
    string Name { get; }

    Task HandleAsync(IEvent evt, int attempt, CancellationToken ct);
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

    Task IEventConsumer.HandleAsync(IEvent evt, int attempt, CancellationToken ct) =>
        evt is TEvent typed
            ? HandleAsync(typed, attempt, ct)
            : throw new InvalidOperationException(
                $"consumer {Name} received {evt.GetType().Name}, expected {typeof(TEvent).Name}");

    /// <summary>
    /// attempt ≥ 1; attempt &gt; 1 means a previous delivery did not finish —
    /// the handler may soften work, not skip it.
    /// </summary>
    protected abstract Task HandleAsync(TEvent evt, int attempt, CancellationToken ct);
}
