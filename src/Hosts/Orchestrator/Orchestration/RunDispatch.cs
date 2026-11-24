using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Context;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>Run rows the Orchestrator created for one claim, ready to hand to the dispatcher
/// (the Orchestrator owns the DB rows; the Runtime API provisions).
/// The Workspace row was created (or reused) by RunPrepare on the Task's first attempt.
/// `AssembledContext` carries the Context Assembler output (null only in legacy
/// tests that never dispatch); the dispatcher freezes it as the Run's immutable snapshot.</summary>
public sealed record RunPreparation(
    Run Run,
    AgentSession Session,
    ContainerInstance Container,
    Workspace Workspace,
    OrchestrationTask Task,
    AgentProfileVersion ProfileVersion,
    ContextAssembly? AssembledContext);

/// <summary>The dispatch hook between the queue and the Runtime API (the Orchestrator owns
/// the DB rows; the Runtime API provisions).
/// <list type="bullet">
/// <item><see cref="HandedOff"/> — `/start` accepted: the job is completed, the Task moved `starting`,
/// the Run mirrored `starting`; the terminal-Run consumer takes over afterwards.</item>
/// <item><see cref="Transient"/> — network/5xx/timeout, nothing was started: the claim is released and
/// the never-dispatched `pending` Run rows rolled back (existing dispatcher contract).</item>
/// <item><see cref="Permanent"/> — the Runtime API rejected the request (400/404/422): cancel-with-recorded-reason
/// via the claim-time path; only when the Run is not already terminal.</item>
/// </list></summary>
public enum DispatchOutcome
{
    HandedOff,
    Transient,
    Permanent,
    /// <summary>The claim was superseded mid-flight (/stop cancelled the claimed Task and
    /// its prepared pending Run rows) — nothing was handed off; the worker releases nothing.</summary>
    Superseded,
}

public interface IRunDispatcher
{
    Task<DispatchOutcome> DispatchAsync(AppDbContext db, RunPreparation preparation, CancellationToken cancellationToken);
}

/// <summary>Service credential for Orchestrator→Runtime API calls (the orchestrator's own
/// service token; the per-Run callback tokens cannot call these endpoints).</summary>
public sealed class RuntimeApiOptions
{
    public string BaseUrl { get; set; } = "http://runtime-api:8080";
    /// <summary>The token presented as `X-Service-Token`; must equal the Runtime API's `ServiceTokens__Orchestrator`.</summary>
    public string ServiceToken { get; set; } = "orchestrator-dev-token";
    /// <summary>Container deployment can take ~a minute; the dispatcher waits accordingly.</summary>
    public int StartTimeoutSeconds { get; set; } = 180;
}

/// <summary>HTTP client for the Runtime API's internal runtime routes.</summary>
public sealed class RuntimeApiClient(HttpClient http, IOptions<RuntimeApiOptions> options)
{
    public async Task<HttpResponseMessage> StartRunAsync(Guid runId, RunStartPayload payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{options.Value.BaseUrl.TrimEnd('/')}/internal/runtime/runs/{runId}/start")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Service-Token", options.Value.ServiceToken);
        return await http.SendAsync(request, cancellationToken);
    }

    /// <summary>`POST /internal/runtime/runs/{runId}/stop`: the graceful-stop
    /// control signal — the Runtime API records it and hands the sequence to the Bridge.</summary>
    public async Task<HttpResponseMessage> StopRunAsync(Guid runId, string reason, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{options.Value.BaseUrl.TrimEnd('/')}/internal/runtime/runs/{runId}/stop")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { reason }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Service-Token", options.Value.ServiceToken);
        return await http.SendAsync(request, cancellationToken);
    }

    public async Task<HttpResponseMessage> CleanupWorkflowAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{options.Value.BaseUrl.TrimEnd('/')}/internal/runtime/workflows/{workflowId}/cleanup")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Service-Token", options.Value.ServiceToken);
        return await http.SendAsync(request, cancellationToken);
    }
}

/// <summary>The start request the Orchestrator sends; `input` is the frozen context package
/// (the assembler's content, byte-identical to RunContextSnapshot.content — tested).</summary>
public sealed record RunStartPayload(
    [property: JsonPropertyName("agentSessionId")] Guid AgentSessionId,
    [property: JsonPropertyName("workspaceId")] Guid WorkspaceId,
    [property: JsonPropertyName("agentProfileVersionId")] Guid AgentProfileVersionId,
    [property: JsonPropertyName("input")] JsonElement Input);

/// <summary>
/// The real dispatcher: freezes the Assembler's context as the
/// Run's IMMUTABLE RunContextSnapshot (idempotent by content hash; never rewritten — an existing row
/// with a different hash aborts the dispatch) and calls the Runtime API
/// `POST /internal/runtime/runs/{runId}/start` with the same frozen content, then completes the claim
/// on success.
/// </summary>
public sealed class RuntimeApiRunDispatcher(RuntimeApiClient client, ILogger<RuntimeApiRunDispatcher> logger) : IRunDispatcher
{
    public async Task<DispatchOutcome> DispatchAsync(AppDbContext db, RunPreparation preparation, CancellationToken cancellationToken)
    {
        var (run, session, container, workspace, task, version, assembled) = preparation;
        // the claim's own write-back (job completion, task transition) happens on the same context the
        // worker gave us, so it cannot be lost on a later release
        var job = await db.OrchestrationJobs
            .SingleOrDefaultAsync(j => j.TaskId == task.Id
                && (j.Status == OrchestrationJobStatuses.Processing || j.Status == OrchestrationJobStatuses.Pending), cancellationToken);
        _ = container; // deconstruction only; task/version below

        if (assembled is null)
            throw new InvalidOperationException(
                $"run {run.Id} dispatch requires the assembled context — the claim path assembles before RunPrepare");

        // /stop cancels a claimed Task (and deletes its pending Run rows) while the claim
        // worker may still be mid-flight between claim and handoff — re-verify before the freeze/POST;
        // a superseded claim removes its just-prepared rows and never touches the Runtime API.
        var freshTaskStatus = await db.OrchestrationTasks.AsNoTracking()
            .Where(t => t.Id == task.Id)
            .Select(t => t.Status)
            .SingleOrDefaultAsync(cancellationToken);
        if (!OrchestrationTaskStatuses.Unfinished.Contains(freshTaskStatus ?? OrchestrationTaskStatuses.Cancelled))
        {
            logger.LogInformation("run {runId} dispatch superseded — task {taskId} is '{status}' (cancelled mid-claim)",
                run.Id, task.Id, freshTaskStatus ?? "missing");
            await db.Runs
                .Where(r => r.TaskId == task.Id && r.Status == RunStatuses.Pending)
                .ExecuteDeleteAsync(cancellationToken);
            return DispatchOutcome.Superseded;
        }

        // The /start payload carries the SAME frozen content (the Runtime API serves /input from the
        // snapshot row, so this field is transport duplication; keeping them byte-identical is tested).
        using var inputDocument = JsonDocument.Parse(assembled.Content);
        var payload = new RunStartPayload(session.Id, workspace.Id, version.Id, inputDocument.RootElement);

        // Freeze BEFORE the POST: /start requires the frozen snapshot to be visible to the Runtime API
        // (its pull route serves this row), and any later claim-time run deletion then cascades it safely.
        ContextAssemblyFreezable.FreezeOrThrow(db, preparation, assembled);
        await db.SaveChangesAsync(cancellationToken);

        HttpResponseMessage response;
        try
        {
            response = await client.StartRunAsync(run.Id, payload, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            // The Runtime API flips the Run out of `pending` inside /start BEFORE responding: a transport
            // failure with the row already beyond `pending` means the request was accepted — releasing the
            // claim would let the lease sweep or a re-claim re-execute the same task (task 16). Recover
            // with the ordinary hand-off write-back; the consumer takes the terminal transition from there.
            var liveStatus = await db.Runs.AsNoTracking()
                .Where(r => r.Id == run.Id)
                .Select(r => r.Status)
                .SingleOrDefaultAsync(cancellationToken);
            if (liveStatus == RunStatuses.Pending)
            {
                logger.LogWarning(exception, "run {runId} dispatch transport failure before acceptance — releasing the claim", run.Id);
                return DispatchOutcome.Transient;
            }
            if (liveStatus is null)
            {
                logger.LogWarning(exception, "run {runId} gone during a failed dispatch (superseded mid-flight)", run.Id);
                return DispatchOutcome.Superseded;
            }
            await CompleteHandoffAsync(db, run, task, job, DateTime.UtcNow, cancellationToken);
            logger.LogWarning(exception, "run {runId} dispatch transport failure after acceptance (run '{status}') — recovered as handed off", run.Id, liveStatus);
            return DispatchOutcome.HandedOff;
        }

        if (response.IsSuccessStatusCode)
        {
            await CompleteHandoffAsync(db, run, task, job, DateTime.UtcNow, cancellationToken);
            logger.LogInformation("run {runId} handed off to the Runtime API (attempt {attempt})", run.Id, run.AttemptNumber);
            return DispatchOutcome.HandedOff;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
            || response.StatusCode == System.Net.HttpStatusCode.RequestTimeout
            || (int)response.StatusCode >= 500)
            return DispatchOutcome.Transient; // retry next claim with the same run rows

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // 409 with an already-terminal Run = idempotent redelivery of a dispatched claim (restart recovery):
            // nothing to hand off, the terminal consumer will process the terminal Run.
            var runTerminal = await db.Runs.AsNoTracking()
                .Where(r => r.Id == run.Id)
                .Select(r => r.Status)
                .SingleOrDefaultAsync(cancellationToken);
            if (runTerminal is RunStatuses.Succeeded or RunStatuses.Failed or RunStatuses.Cancelled or RunStatuses.TimedOut)
            {
                // the Run is terminal: nothing to hand off, but the claim's own write-back (Task `starting`,
                // job `completed`) must still land so the terminal-Run consumer adopts it (task 16) instead
                // of waiting for a lease-expiry recovery
                await CompleteHandoffAsync(db, run, task, job, DateTime.UtcNow, cancellationToken);
                return DispatchOutcome.HandedOff;
            }
            logger.LogWarning("run {runId} dispatch conflict: {body}", run.Id, errorBody);
            return DispatchOutcome.Permanent;
        }

        logger.LogWarning("run {runId} dispatch rejected ({statusCode}): {body}", run.Id, (int)response.StatusCode, errorBody);
        return DispatchOutcome.Permanent;
    }

    /// <summary>
    /// The hand-off write-back every accepted dispatch must persist (task 16: identical for the success
    /// path, the transport-failure recovery, and the 409-with-terminal-Run recovery). The Run is pinned
    /// `starting` with a conditional WHERE — a fast-settling container (cached image) can deliver its
    /// terminal callback before this lands, and `starting` must never clobber a terminal Run state nor an
    /// event-driven `running`. Idempotent: re-running after a partial write-back converges.
    /// </summary>
    private static async Task CompleteHandoffAsync(
        AppDbContext db, Run run, OrchestrationTask task, OrchestrationJob? job, DateTime now, CancellationToken cancellationToken)
    {
        await db.Runs
            .Where(r => r.Id == run.Id && (r.Status == RunStatuses.Pending || r.Status == RunStatuses.Starting)
                && r.Status != RunStatuses.Running) // never downgrade an event-driven Running run
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.Status, RunStatuses.Starting)
                .SetProperty(r => r.StartedAt, now), cancellationToken);
        task.Status = OrchestrationTaskStatuses.Starting; // the terminal-Run consumer applies the terminal task state
        task.StartedAt = task.StartedAt ?? now;
        if (job is { } dispatchJob)
        {
            dispatchJob.Status = OrchestrationJobStatuses.Completed;
            dispatchJob.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);

        // every accepted handoff (including the restart-recovery 409 and the transport-failure-after-
        // acceptance paths) marks the item AI Processing; the terminal-Run consumer follows with the
        // terminal label through the outbox's per-work-item ordering, so the final state stays right.
        var handoffWorkflow = await db.Workflows.AsNoTracking()
            .SingleAsync(w => w.Id == task.WorkflowId, cancellationToken);
        var handoffItem = await db.WorkItems.AsNoTracking()
            .SingleAsync(w => w.Id == handoffWorkflow.WorkItemId, cancellationToken);
        await AiStateLabels.MarkAsync(db, handoffItem.RepositoryId, handoffItem.Id, handoffItem.Number,
            task.Id, AiStateLabels.Processing, cancellationToken);
    }
}
