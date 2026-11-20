using PiAgent.Infrastructure.Persistence;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>Run rows the Orchestrator created for one claim, ready to hand to the dispatcher
/// (decision 44: the Orchestrator owns the DB rows; the Runtime API — ticket 09 — provisions).</summary>
public sealed record RunPreparation(
    Run Run,
    AgentSession Session,
    ContainerInstance Container,
    OrchestrationTask Task,
    AgentProfileVersion ProfileVersion);

/// <summary>
/// The dispatch hook between the queue and the Runtime API. V1/ticket-09 contract: `true` = handed off
/// (or a stub no-op) — the claim stays; `false` = transient dispatch failure, nothing was started — the
/// claim is released and the pending Run rows rolled back. Ticket 09 replaces `NullRunDispatcher`.
/// </summary>
public interface IRunDispatcher
{
    Task<bool> DispatchAsync(AppDbContext db, RunPreparation preparation, CancellationToken cancellationToken);
}

/// <summary>Stub until ticket 09 wires the Runtime API: does nothing, the prepared Run keeps its
/// `pending` status and the claimed Task keeps waiting; lease expiry rolls the claim back.</summary>
public sealed class NullRunDispatcher : IRunDispatcher
{
    public Task<bool> DispatchAsync(AppDbContext db, RunPreparation preparation, CancellationToken cancellationToken) =>
        Task.FromResult(true);
}
