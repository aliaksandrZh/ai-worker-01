namespace PiAgent.Events;

/// <summary>
/// A read-only view of already-recorded events: the events table is append-only for
/// producers, so this narrow interface is the one sanctioned read path. The Orchestrator's
/// comment aggregation (docs/incident-2026-11-26-follow-ups.md Block 3.3b) reads a
/// reviewer run's submitted comments from the log instead of storing them anywhere new.
/// </summary>
public interface IEventReader
{
    /// <summary>
    /// Already-recorded events of type <typeparamref name="T"/> whose payload's
    /// <c>runId</c> equals <paramref name="runId"/>, oldest first.
    /// </summary>
    Task<IReadOnlyList<T>> ReadByRunIdAsync<T>(long runId, CancellationToken ct) where T : IEvent;
}
