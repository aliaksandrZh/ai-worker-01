# Event Log — interface contract

> The deep module behind the event-driven redesign ([ADR-0002](adr/0002-event-log.md)).
> **Interfaces only — no implementation.** The event type vocabulary is
> [flows/events.md](flows/events.md); this document fixes the delivery contract: what
> callers may rely on, what the implementation must hide.

## Interface

```csharp
namespace PiAgent.Events;

/// A typed event payload. Each type declares its own stream key —
/// consumers never deal with streams.
public interface IEvent
{
    string StreamKey { get; }
}

/// The log position of an appended event.
public interface IEventLog
{
    /// Fire-and-commit append. One INSERT; the event is visible to consumers
    /// once the call returns.
    Task<long> AppendAsync<TEvent>(TEvent evt, CancellationToken ct)
        where TEvent : IEvent;

    /// Transactional append: the event commits atomically with the caller's own
    /// state change in the same Postgres transaction. This is how a state change
    /// and the event that announces it never disagree — the outbox pattern,
    /// absorbed into the module.
    Task<long> AppendAsync<TEvent>(TEvent evt, IEventTransaction tx, CancellationToken ct)
        where TEvent : IEvent;
}

/// A unit of orchestration work bound to the current transaction.
public interface IEventTransaction { /* created inside the module, e.g. via factory */ }
```

Consumers never call the log to receive — they subscribe:

```csharp
/// The worker loop is inside the module. A consumer declares what it eats
/// and handles events; everything else is the EventLog's business.
public abstract class EventConsumer<TEvent> : IEventConsumer where TEvent : IEvent
{
    public Type[] Consumes { get; } = [typeof(TEvent)];

    /// attempt >= 1; attempt > 1 means a previous delivery did not finish —
    /// the handler may soften work, not skip it.
    protected abstract Task HandleAsync(TEvent evt, int attempt, CancellationToken ct);
}
```

Wiring, one extension per module like the reference repo's `DependencyInjection.cs`:

```csharp
services.AddEventLog(configuration);                 // delivery + poll loops
services.AddEventConsumer<PollerConsumer>(config);   // started only if Workers:{Name}:Enabled
```

## Invariants

1. **At-least-once.** A handler may see the same event more than once; handlers are
   idempotent. The `attempt` parameter says which delivery it is.
2. **Visibility timeout.** A claimed event is invisible for at most the configured
   timeout; a crashed or stalled handler's event is redelivered after it. After
   `MaxAttempts` failed deliveries the event moves to the dead-letter store; a handler
   may also declare a failure permanent (`PoisonAsync`) — the event dead-letters
   immediately, because no retry can succeed.
3. **Dead-letter is inspectable and replayable.** Parked events stay; replay re-enters
   the log with attempt reset. Dead-lettering also emits a user-facing notification.
4. **Strict order inside a stream, parallel across streams.** While a stream's head
   event is being processed, nothing later in that stream starts — a slow handler
   delays only its own stream until the visibility timeout releases it.
5. **Multi-instance safe.** Several instances may run the same consumer; at any moment
   at most one instance processes a given stream head. This is the claim mechanism, not
   a distributed lock the callers know about.
6. **Transactional append.** State change + announcing event commit together; no event
   may announce state that did not commit, no state change may go unannounced.
7. **Consumers are config-gated.** `Workers:{Name}:Enabled` decides startup — per
   instance, per consumer; the code never enumerates consumers itself. Workers poll;
   latency budget = `PollInterval`. LISTEN/NOTIFY is a future implementation swap, not
   an interface change.
8. **Batch is a config knob** (`Batch`), no adaptive sizing.

## Configuration knobs

| Knob | Meaning | Default posture |
|---|---|---|
| `Workers:{Name}:Enabled` | start this consumer on this instance | off |
| `PollInterval` | poll cadence for all consumers | short (seconds) |
| `VisibilityTimeout` | how long a claimed event is invisible | generous enough for the slowest handler |
| `MaxAttempts` | deliveries before dead-letter | small; dead-letter exists for the odd case |
| `Batch` | events claimed per loop iteration | small |

## What the implementation hides

Tables, cursors, claim SQL (`SKIP LOCKED`), JSON serialization, retry bookkeeping,
dead-letter storage, poison detection, clock skew. Callers see only: append, subscribe,
handle.
