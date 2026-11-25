using PiAgent.Events.Delivery;

namespace PiAgent.Events.InMemory;

/// <summary>
/// The second adapter at the EventLog seam (docs/eventlog.md): same semantics as the
/// Postgres store — at-least-once, per-stream head-of-line, dead-letter — without a
/// database. Tests drive delivery deterministically through RunPendingAsync.
/// </summary>
public sealed class InMemoryEventStore : IEventLog, IEventQueue
{
    private readonly object _lock = new();
    private readonly List<Row> _events = [];
    private readonly List<DeadLetterRow> _deadLetters = [];
    private long _nextId = 1;

    private sealed class Row(long id, IEvent evt)
    {
        public long Id { get; } = id;
        public IEvent Event { get; } = evt;
        public bool Processed { get; set; }
        public DateTime? ClaimedUntilUtc { get; set; }
        public int Attempts { get; set; }
    }

    private sealed class DeadLetterRow(long id, IEvent evt, int attempts, string error)
    {
        public long Id { get; } = id;
        public IEvent Event { get; } = evt;
        public int Attempts { get; } = attempts;
        public string Error { get; } = error;
        public DateTime DeadAtUtc { get; } = DateTime.UtcNow;
    }

    // ---- producer side ---------------------------------------------------------

    public Task<long> AppendAsync<TEvent>(TEvent evt, CancellationToken ct) where TEvent : IEvent
    {
        lock (_lock)
        {
            var id = _nextId++;
            _events.Add(new Row(id, evt));
            return Task.FromResult(id);
        }
    }

    public Task<long> AppendAsync<TEvent>(TEvent evt, IEventTransaction tx, CancellationToken ct)
        where TEvent : IEvent
    {
        // a foreign transaction (another adapter's unit of work) is a no-op here —
        // the in-memory store is never its commit target
        return tx is InMemoryTransaction own
            ? own.Enlist(evt, this)
            : AppendAsync(evt, ct);
    }

    public Task<IEventTransaction> BeginTransactionAsync(CancellationToken ct) =>
        Task.FromResult<IEventTransaction>(new InMemoryTransaction());

    private sealed class InMemoryTransaction : IEventTransaction
    {
        private readonly List<(IEvent Event, InMemoryEventStore Owner)> _pending = [];

        public Task<long> Enlist<TEvent>(TEvent evt, InMemoryEventStore owner) where TEvent : IEvent
        {
            lock (owner._lock)
            {
                var id = owner._nextId++;
                _pending.Add((evt, owner));
                return Task.FromResult(id);
            }
        }

        public Task CommitAsync(CancellationToken ct)
        {
            foreach (var (evt, owner) in _pending)
            {
                lock (owner._lock)
                {
                    var id = owner._nextId++;
                    owner._events.Add(new Row(id, evt));
                }
            }

            _pending.Clear();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ---- delivery side ---------------------------------------------------------

    public Task<IReadOnlyList<ClaimedEvent>> ClaimAsync(
        IReadOnlyCollection<string> types, int batch, TimeSpan visibility, CancellationToken ct)
    {
        lock (_lock)
        {
            var typeSet = types.ToHashSet();
            var claimed = new List<ClaimedEvent>();

            // per stream, only the head: no older unprocessed event of the same stream
            foreach (var stream in _events
                         .Where(e => !e.Processed && typeSet.Contains(e.Event.GetType().Name))
                         .Select(e => e.Event.StreamKey)
                         .Distinct())
            {
                var streamRows = _events
                    .Where(e => e.Event.StreamKey == stream && typeSet.Contains(e.Event.GetType().Name))
                    .OrderBy(e => e.Id)
                    .ToList();

                var head = streamRows.First(r => !r.Processed);
                if (head.ClaimedUntilUtc is { } until && until > DateTime.UtcNow)
                    continue;

                head.ClaimedUntilUtc = DateTime.UtcNow + visibility;
                head.Attempts++;
                claimed.Add(new ClaimedEvent(head.Id, head.Event, head.Attempts));
                if (claimed.Count >= batch)
                    break;
            }

            return Task.FromResult<IReadOnlyList<ClaimedEvent>>(claimed);
        }
    }

    public Task AckAsync(long id, CancellationToken ct)
    {
        lock (_lock)
        {
            _events.First(e => e.Id == id).Processed = true;
            return Task.CompletedTask;
        }
    }

    public Task FailAsync(long id, string error, int maxAttempts, CancellationToken ct)
    {
        lock (_lock)
        {
            var row = _events.First(e => e.Id == id);
            row.ClaimedUntilUtc = null;
            if (row.Attempts < maxAttempts)
                return Task.CompletedTask;

            _events.Remove(row);
            _deadLetters.Add(new DeadLetterRow(_nextId++, row.Event, row.Attempts, error));
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<DeadLetterEntry>> ListDeadLettersAsync(int limit, CancellationToken ct)
    {
        lock (_lock)
        {
            IReadOnlyList<DeadLetterEntry> entries = _deadLetters
                .Take(limit)
                .Select(d => new DeadLetterEntry(
                    d.Id, d.Event.GetType().Name, d.Event.StreamKey, d.Attempts, d.Error, d.DeadAtUtc))
                .ToList();
            return Task.FromResult(entries);
        }
    }

    public Task PoisonAsync(long id, string error, CancellationToken ct)
    {
        lock (_lock)
        {
            var row = _events.FirstOrDefault(e => e.Id == id);
            if (row is null)
                return Task.CompletedTask;

            _events.Remove(row);
            _deadLetters.Add(new DeadLetterRow(_nextId++, row.Event, row.Attempts, error));
            return Task.CompletedTask;
        }
    }

    public Task<bool> ReplayDeadLetterAsync(long deadLetterId, CancellationToken ct)
    {
        lock (_lock)
        {
            var parked = _deadLetters.FirstOrDefault(d => d.Id == deadLetterId);
            if (parked is null)
                return Task.FromResult(false);

            _deadLetters.Remove(parked);
            _events.Add(new Row(_nextId++, parked.Event));
            return Task.FromResult(true);
        }
    }

    // ---- test surface ----------------------------------------------------------

    /// <summary>Number of events not yet processed (claimed ones included).</summary>
    public int PendingCount
    {
        get
        {
            lock (_lock) return _events.Count(e => !e.Processed);
        }
    }

    /// <summary>All appended events in append order (test surface).</summary>
    public IReadOnlyList<IEvent> Appended
    {
        get
        {
            lock (_lock) return _events.Select(e => e.Event).ToList();
        }
    }

    /// <summary>Advance the clock: redeliver everything whose claim expired.</summary>
    public void ExpireClaims() => ExpireClaims(TimeSpan.FromMinutes(10));

    public void ExpireClaims(TimeSpan after)
    {
        lock (_lock)
        {
            foreach (var row in _events.Where(e =>
                         !e.Processed && e.ClaimedUntilUtc <= DateTime.UtcNow - after))
                row.ClaimedUntilUtc = null;
        }
    }
}
