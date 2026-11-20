using System.Collections.Concurrent;

namespace PiAgent.Orchestrator.Commands;

/// <summary>
/// Command pipeline counters (§14: Orchestrator observes "commands, authorization, state transitions").
/// Read by OrchestratorMetrics; not per-run gauges, cumulative process counters.
/// </summary>
public sealed class CommandMetrics
{
    private readonly ConcurrentDictionary<string, long> _rejectionsByReason = new();

    private long _received;
    private long _authorized;
    private long _rejected;
    private long _authorizationSystemErrors;

    public long CommandsReceivedTotal => Interlocked.Read(ref _received);
    public long CommandsAuthorizedTotal => Interlocked.Read(ref _authorized);
    public long CommandsRejectedTotal => Interlocked.Read(ref _rejected);
    public long AuthorizationSystemErrorsTotal => Interlocked.Read(ref _authorizationSystemErrors);
    public IReadOnlyDictionary<string, long> RejectionsByReason => new ConcurrentDictionary<string, long>(_rejectionsByReason);

    public void RecordReceived() => Interlocked.Increment(ref _received);
    public void RecordAuthorized() => Interlocked.Increment(ref _authorized);
    public void RecordRejected(string reason)
    {
        Interlocked.Increment(ref _rejected);
        _rejectionsByReason.AddOrUpdate(reason, 1, static (_, count) => count + 1);
    }
    public void RecordAuthorizationSystemError() => Interlocked.Increment(ref _authorizationSystemErrors);
}
