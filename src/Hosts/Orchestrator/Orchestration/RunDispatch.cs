using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Infrastructure.Persistence;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>Run rows the Orchestrator created for one claim, ready to hand to the dispatcher
/// (decision 44: the Orchestrator owns the DB rows; the Runtime API — ticket 09 — provisions).
/// The Workspace row was created (or reused) by RunPrepare on the Task's first attempt.</summary>
public sealed record RunPreparation(
    Run Run,
    AgentSession Session,
    ContainerInstance Container,
    Workspace Workspace,
    OrchestrationTask Task,
    AgentProfileVersion ProfileVersion);

/// <summary>The dispatch hook between the queue and the Runtime API (decision 44: the Orchestrator owns
/// the DB rows; the Runtime API provisions). Ticket 09 replaces the task-07 no-op stub.
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
}

public interface IRunDispatcher
{
    Task<DispatchOutcome> DispatchAsync(AppDbContext db, RunPreparation preparation, CancellationToken cancellationToken);
}

/// <summary>Decision-65 service credential for Orchestrator→Runtime API calls (the orchestrator's own
/// service token; the per-Run callback tokens cannot call these endpoints).</summary>
public sealed class RuntimeApiOptions
{
    public string BaseUrl { get; set; } = "http://runtime-api:8080";
    /// <summary>The token presented as `X-Service-Token`; must equal the Runtime API's `ServiceTokens__Orchestrator`.</summary>
    public string ServiceToken { get; set; } = "orchestrator-dev-token";
    /// <summary>Container deployment can take ~a minute; the dispatcher waits accordingly.</summary>
    public int StartTimeoutSeconds { get; set; } = 180;
}

/// <summary>HTTP client for the Runtime API's internal runtime routes (04 §3).</summary>
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
}

public sealed record RunStartPayload(
    [property: JsonPropertyName("agentSessionId")] Guid AgentSessionId,
    [property: JsonPropertyName("workspaceId")] Guid WorkspaceId,
    [property: JsonPropertyName("agentProfileVersionId")] Guid AgentProfileVersionId,
    [property: JsonPropertyName("input")] RunInput Input);

public sealed record RunInput(
    [property: JsonPropertyName("workItem")] RunInputWorkItem WorkItem,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("instructions")] string Instructions,
    [property: JsonPropertyName("artifactReferences")] Guid[] ArtifactReferences);

public sealed record RunInputWorkItem(
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("number")] long Number,
    [property: JsonPropertyName("title")] string? Title);

/// <summary>
/// The real dispatcher (ticket 09): freezes the Run's Context snapshot (idempotent, ticket-11 scope:
/// minimal assembly of the command + work item + artifact references; no pruning), then calls the
/// Runtime API `POST /internal/runtime/runs/{runId}/start` and completes the claim on success.
/// </summary>
public sealed class RuntimeApiRunDispatcher(RuntimeApiClient client, ILogger<RuntimeApiRunDispatcher> logger) : IRunDispatcher
{
    public async Task<DispatchOutcome> DispatchAsync(AppDbContext db, RunPreparation preparation, CancellationToken cancellationToken)
    {
        var (run, session, container, workspace, task, version) = preparation;
        // the claim's own write-back (job completion, task transition) happens on the same context the
        // worker gave us, so it cannot be lost on a later release
        var job = await db.OrchestrationJobs
            .SingleOrDefaultAsync(j => j.TaskId == task.Id
                && (j.Status == OrchestrationJobStatuses.Processing || j.Status == OrchestrationJobStatuses.Pending), cancellationToken);
        _ = workspace; // deconstructed for task/version below

        var payload = await BuildStartPayloadAsync(db, preparation, cancellationToken);
        var snapshot = await db.RunContextSnapshots.SingleOrDefaultAsync(s => s.RunId == run.Id, cancellationToken);
        var content = JsonSerializer.Serialize(payload.Payload.Input);
        if (snapshot is null)
        {
            db.RunContextSnapshots.Add(new RunContextSnapshot
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                TaskId = task.Id,
                ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
                IncludedSources = JsonSerializer.Serialize(payload.IncludedSources),
                ExcludedSources = "[]",
                Content = content,
                CreatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            snapshot.Content = content;
            snapshot.ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            snapshot.IncludedSources = JsonSerializer.Serialize(payload.IncludedSources);
            snapshot.ExcludedSources = "[]";
        }
        // Commit BEFORE the POST: /start requires the frozen snapshot to be visible to the Runtime API
        // (its pull route serves this row), and any later claim-time run deletion then cascades it safely.
        await db.SaveChangesAsync(cancellationToken);

        HttpResponseMessage response;
        try
        {
            response = await client.StartRunAsync(run.Id, payload.Payload, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            logger.LogWarning(exception, "run {runId} dispatch transport failure — releasing the claim", run.Id);
            return DispatchOutcome.Transient;
        }

        if (response.IsSuccessStatusCode)
        {
            var now = DateTime.UtcNow;
            // Atomic write-back: a fast-settling container (cached image) can deliver the terminal callback
            // before this lands — the WHERE keeps `starting` from ever clobbering a terminal Run state.
            await db.Runs
                .Where(r => r.Id == run.Id && (r.Status == RunStatuses.Pending || r.Status == RunStatuses.Starting))
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
                return DispatchOutcome.HandedOff;
            logger.LogWarning("run {runId} dispatch conflict: {body}", run.Id, errorBody);
            return DispatchOutcome.Permanent;
        }

        logger.LogWarning("run {runId} dispatch rejected ({statusCode}): {body}", run.Id, (int)response.StatusCode, errorBody);
        return DispatchOutcome.Permanent;
    }

    /// <summary>Ticket-11 scope: the command text, work item identity, and linked artifact references —
    /// the frozen input the Bridge renders and prompts. No pruning machinery yet (nothing pruned).</summary>
    internal static async Task<(RunStartPayload Payload, string[] IncludedSources)> BuildStartPayloadAsync(
        AppDbContext db, RunPreparation preparation, CancellationToken cancellationToken)
    {
        var (run, session, container, workspace, task, version) = preparation;
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == task.WorkflowId, cancellationToken);
        var workItem = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == workflow.WorkItemId, cancellationToken);
        var repository = await db.Repositories.AsNoTracking().SingleAsync(r => r.Id == workItem.RepositoryId, cancellationToken);
        var command = task.TriggerCommandId is { } commandId
            ? await db.AgentCommands.AsNoTracking().SingleAsync(c => c.Id == commandId, cancellationToken)
            : null;
        var artifactIds = await db.TaskArtifactInputs.AsNoTracking()
            .Where(i => i.TaskId == task.Id)
            .Select(i => i.ArtifactId)
            .ToListAsync(cancellationToken);

        var instructions = command?.SourceBodySnapshot;
        if (string.IsNullOrWhiteSpace(instructions))
            instructions = $"Execute the '{task.Type}' task for {repository.FullName}#{workItem.Number}.";

        var input = new RunInput(
            new RunInputWorkItem(repository.FullName, workItem.Number, workItem.Title),
            command?.CommandName ?? task.Type,
            instructions,
            artifactIds.ToArray());
        var payload = new RunStartPayload(session.Id, workspace.Id, version.Id, input);
        var sources = new List<string>
        {
            $"{workItem.Type}:{repository.FullName}#{workItem.Number}",
        };
        if (command is not null)
            sources.Add($"command:{command.Id}");
        sources.AddRange(artifactIds.Select(artifactId => $"artifact:{artifactId}"));
        return (payload, sources.ToArray());
    }
}
