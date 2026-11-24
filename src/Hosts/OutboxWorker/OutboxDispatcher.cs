using PiAgent.OutboxWorker.GitHub;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Workers;

namespace PiAgent.OutboxWorker;

/// <summary>
/// One OutboxAction per WorkerLoop tick. Claim via the store's DB lease, then:
/// retry ⇒ invisible-marker check first (duplicate suppression), then POST the comment. Permanent
/// GitHub statuses (401, 404, 422, regular 403) dead-letter immediately; transient ones (5xx, 429,
/// rate-limit 403) retry with exponential backoff capped at 1 h over the `outbox_retry_window`;
/// network-level failures mark the attempt ambiguous. GET failures before a re-send are left to
/// lease-expiry recovery (the claim is still owned here; nothing is written).
/// </summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    GitHubWriteClient client,
    IOptions<OutboxWorkerOptions> options,
    OutboxState outboxState,
    ILogger<OutboxDispatcher> logger) : IWorkerCycle
{
    private readonly Guid _dispatcherId = Guid.NewGuid();
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(8);

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<OutboxActionStore>();

        var action = await store.ClaimNextAsync(_dispatcherId, Lease, options.Value.RetryWindow, cancellationToken);
        if (action is not null)
            await DeliverAsync(store, db, action, cancellationToken);

        await RefreshStateAsync(db, cancellationToken);
    }

    private async Task DeliverAsync(OutboxActionStore store, AppDbContext db, OutboxAction action, CancellationToken cancellationToken)
    {
        if (action.ActionType == OutboxActionTypes.AiLabel)
        {
            await DeliverAiLabelAsync(store, db, action, cancellationToken);
            return;
        }
        if (action.ActionType == OutboxActionTypes.Label)
        {
            await DeliverLabelAsync(store, db, action, cancellationToken);
            return;
        }
        if (action.ActionType == OutboxActionTypes.ProjectStatus)
        {
            await DeliverProjectStatusAsync(store, db, action, cancellationToken);
            return;
        }

        var payload = ParsePayload(action.Payload);
        if (payload is null)
        {
            var reason = $"payload is not a valid outbox comment payload: {Summarize(action.Payload)}";
            await store.MarkDeadLetterAsync(action.Id, _dispatcherId, reason, cancellationToken);
            logger.LogWarning("outbox dead_letter {action_id} after {attempts} attempts: {error}",
                action.Id, action.Attempts, reason);
            return;
        }

        var (issueNumber, text) = payload.Value;
        var repository = await db.Repositories.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == action.RepositoryId, cancellationToken);
        if (repository is null)
        {
            var reason = $"outbox action {action.Id}: repository {action.RepositoryId} no longer exists";
            await store.MarkDeadLetterAsync(action.Id, _dispatcherId, reason, cancellationToken);
            logger.LogWarning("outbox dead_letter {action_id}: {error}", action.Id, reason);
            return;
        }

        try
        {
            var marker = Marker(action.IdempotencyKey);
            if (action.Attempts > 1)
            {
                // duplicate control: before every RE-send (attempts ≥ 2 — ClaimNextAsync
                // returns the row with the current attempt already counted), look for our marker in
                // the newest comments: the previous attempt may have landed despite reporting failure.
                // GET failures classify identically to write failures (same PAT, same GitHub). The
                // first attempt never probes: nothing was sent yet.
                var bodies = await client.ListLatestCommentBodiesAsync(repository.Owner, repository.Name, issueNumber, cancellationToken);
                if (bodies.Any(body => body.Contains(marker, StringComparison.Ordinal)))
                {
                    await store.MarkCompletedAsync(action.Id, _dispatcherId, cancellationToken);
                    logger.LogInformation("outbox duplicate suppressed {idempotency_key} (action {action_id})",
                        action.IdempotencyKey, action.Id);
                    return;
                }
            }

            await client.CreateCommentAsync(repository.Owner, repository.Name, issueNumber, marker + "\n\n" + text, cancellationToken);
            await store.MarkCompletedAsync(action.Id, _dispatcherId, cancellationToken);
            logger.LogInformation("outbox comment delivered {action_id} to {repo}/{issue_number} (attempts {attempts})",
                action.Id, repository.FullName, issueNumber, action.Attempts);
        }
        catch (GitHubDeliveryException exception)
        {
            if (exception.Status is null)
            {
                var error = $"ambiguous: {exception.Message}";
                await store.MarkTransientFailureAsync(action.Id, _dispatcherId, error, Backoff(action.Attempts), cancellationToken);
                logger.LogWarning("outbox ambiguous delivery {action_id} (attempt {attempts}): {error}",
                    action.Id, action.Attempts, error);
            }
            else if (IsPermanent(exception))
            {
                var error = exception.Message;
                await store.MarkDeadLetterAsync(action.Id, _dispatcherId, error, cancellationToken);
                logger.LogWarning("outbox dead_letter {action_id} after {attempts} attempts: {error}",
                    action.Id, action.Attempts, error);
            }
            else
            {
                var delay = Backoff(action.Attempts);
                if (exception.RetryAfterSeconds is { } retryAfter)
                    delay = TimeSpan.FromTicks(Math.Min(MaxInterval.Ticks, Math.Max(delay.Ticks, TimeSpan.FromSeconds(retryAfter).Ticks)));
                await store.MarkTransientFailureAsync(action.Id, _dispatcherId, exception.Message, delay, cancellationToken);
                logger.LogWarning("outbox transient delivery failure {action_id} (attempt {attempts}) retry in {delay}: {error}",
                    action.Id, action.Attempts, delay, exception.Message);
            }
        }
    }

    /// <summary>The AI-label transition: no marker/duplicate-suppression probe is needed — the
    /// apply is idempotent by construction (remove-other-three + add-target; re-delivery after an
    /// ambiguous failure re-applies the same state). Error classification is identical to comments:
    /// GitHub statuses the client did not translate to success go through the same permanent/transient split.</summary>
    private async Task DeliverAiLabelAsync(OutboxActionStore store, AppDbContext db, OutboxAction action,
        CancellationToken cancellationToken)
    {
        var (issueNumber, target) = ParseAiLabelPayload(action.Payload);
        if (issueNumber is null)
        {
            var reason = $"payload is not a valid ai_label payload: {Summarize(action.Payload)}";
            await store.MarkDeadLetterAsync(action.Id, _dispatcherId, reason, cancellationToken);
            logger.LogWarning("outbox dead_letter {action_id} after {attempts} attempts: {error}",
                action.Id, action.Attempts, reason);
            return;
        }

        var repository = await db.Repositories.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == action.RepositoryId, cancellationToken);
        if (repository is null)
        {
            var reason = $"outbox action {action.Id}: repository {action.RepositoryId} no longer exists";
            await store.MarkDeadLetterAsync(action.Id, _dispatcherId, reason, cancellationToken);
            logger.LogWarning("outbox dead_letter {action_id}: {error}", action.Id, reason);
            return;
        }

        try
        {
            await client.ApplyAiStateLabelAsync(repository.Owner, repository.Name, issueNumber.Value, target, cancellationToken);
            await store.MarkCompletedAsync(action.Id, _dispatcherId, cancellationToken);
            logger.LogInformation("outbox ai_label delivered {action_id} to {repo}/{issue_number}: {label} (attempts {attempts})",
                action.Id, repository.FullName, issueNumber, target ?? "(clear)", action.Attempts);
        }
        catch (GitHubDeliveryException exception)
        {
            if (exception.Status is null)
            {
                var error = $"ambiguous: {exception.Message}";
                await store.MarkTransientFailureAsync(action.Id, _dispatcherId, error, Backoff(action.Attempts), cancellationToken);
                logger.LogWarning("outbox ambiguous ai_label delivery {action_id} (attempt {attempts}): {error}",
                    action.Id, action.Attempts, error);
            }
            else if (IsPermanent(exception))
            {
                await store.MarkDeadLetterAsync(action.Id, _dispatcherId, exception.Message, cancellationToken);
                logger.LogWarning("outbox dead_letter {action_id} after {attempts} attempts: {error}",
                    action.Id, action.Attempts, exception.Message);
            }
            else
            {
                var delay = Backoff(action.Attempts);
                if (exception.RetryAfterSeconds is { } retryAfter)
                    delay = TimeSpan.FromTicks(Math.Min(MaxInterval.Ticks, Math.Max(delay.Ticks, TimeSpan.FromSeconds(retryAfter).Ticks)));
                await store.MarkTransientFailureAsync(action.Id, _dispatcherId, exception.Message, delay, cancellationToken);
                logger.LogWarning("outbox transient ai_label delivery failure {action_id} (attempt {attempts}) retry in {delay}: {error}",
                    action.Id, action.Attempts, delay, exception.Message);
            }
        }
    }

    /// <summary>The board status move: idempotent by construction (setting the same single-select
    /// value twice converges), so no marker/duplicate-suppression probe is needed. Error
    /// classification is identical to labels: permanent statuses (including the client's own 422
    /// for a renamed/removed column or option) dead-letter, transient ones retry with backoff.</summary>
    private async Task DeliverProjectStatusAsync(OutboxActionStore store, AppDbContext db, OutboxAction action,
        CancellationToken cancellationToken)
    {
        var payload = ParseProjectStatusPayload(action.Payload);
        if (payload is null)
        {
            var reason = $"payload is not a valid project_status payload: {Summarize(action.Payload)}";
            await store.MarkDeadLetterAsync(action.Id, _dispatcherId, reason, cancellationToken);
            logger.LogWarning("outbox dead_letter {action_id} after {attempts} attempts: {error}",
                action.Id, action.Attempts, reason);
            return;
        }

        var repository = await db.Repositories.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == action.RepositoryId, cancellationToken);
        if (repository is null)
        {
            var reason = $"outbox action {action.Id}: repository {action.RepositoryId} no longer exists";
            await store.MarkDeadLetterAsync(action.Id, _dispatcherId, reason, cancellationToken);
            logger.LogWarning("outbox dead_letter {action_id}: {error}", action.Id, reason);
            return;
        }

        var (owner, projectNumber, itemId, itemNodeId, field, status) = payload.Value;
        try
        {
            await client.SetProjectItemStatusAsync(owner, projectNumber, itemNodeId, field, status, cancellationToken);
            await store.MarkCompletedAsync(action.Id, _dispatcherId, cancellationToken);
            logger.LogInformation(
                "outbox project_status delivered {action_id} to board {owner}/{project_number} item {item_id} ({item_node_id}): {field} = {status} (attempts {attempts})",
                action.Id, owner, projectNumber, itemId, itemNodeId, field, status, action.Attempts);
        }
        catch (GitHubDeliveryException exception)
        {
            if (exception.Status is null)
            {
                var error = $"ambiguous: {exception.Message}";
                await store.MarkTransientFailureAsync(action.Id, _dispatcherId, error, Backoff(action.Attempts), cancellationToken);
                logger.LogWarning("outbox ambiguous project_status delivery {action_id} (attempt {attempts}): {error}",
                    action.Id, action.Attempts, error);
            }
            else if (IsPermanent(exception))
            {
                await store.MarkDeadLetterAsync(action.Id, _dispatcherId, exception.Message, cancellationToken);
                logger.LogWarning("outbox dead_letter {action_id} after {attempts} attempts: {error}",
                    action.Id, action.Attempts, exception.Message);
            }
            else
            {
                var delay = Backoff(action.Attempts);
                if (exception.RetryAfterSeconds is { } retryAfter)
                    delay = TimeSpan.FromTicks(Math.Min(MaxInterval.Ticks, Math.Max(delay.Ticks, TimeSpan.FromSeconds(retryAfter).Ticks)));
                await store.MarkTransientFailureAsync(action.Id, _dispatcherId, exception.Message, delay, cancellationToken);
                logger.LogWarning("outbox transient project_status delivery failure {action_id} (attempt {attempts}) retry in {delay}: {error}",
                    action.Id, action.Attempts, delay, exception.Message);
            }
        }
    }

    /// <summary>Payload contract: `{ "owner": string, "project_number": int, "item_id": long,
    /// "item_node_id": string, "field": string, "status": string }`. `item_node_id` is the GraphQL
    /// GLOBAL node id (PVTI_…): the mutation resolves nothing by database id — a payload without it
    /// is unparsable (dead-letter), never a retry.</summary>
    private static (string Owner, int ProjectNumber, long ItemId, string ItemNodeId, string Field, string Status)?
        ParseProjectStatusPayload(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var owner = root.GetProperty("owner").GetString();
            var projectNumber = root.GetProperty("project_number").GetInt32();
            var itemId = root.GetProperty("item_id").GetInt64();
            var itemNodeId = root.GetProperty("item_node_id").GetString();
            var field = root.GetProperty("field").GetString();
            var status = root.GetProperty("status").GetString();
            if (string.IsNullOrWhiteSpace(owner) || projectNumber <= 0 || itemId <= 0
                || string.IsNullOrWhiteSpace(itemNodeId)
                || string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(status))
                return null;
            return (owner, projectNumber, itemId, itemNodeId, field, status);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Payload contract: `{ "issue_number": long, "label": string|null }` — null label = clear.</summary>
    /// <summary>The named-label add/remove: no marker/duplicate-suppression probe is needed —
    /// the apply is idempotent by construction (POST on a present label converges, DELETE on an
    /// absent one answers 404 = fine). Error classification is identical to the AI labels.</summary>
    private async Task DeliverLabelAsync(OutboxActionStore store, AppDbContext db, OutboxAction action,
        CancellationToken cancellationToken)
    {
        var (issueNumber, label, add) = ParseLabelPayload(action.Payload);
        if (issueNumber is null || label is null)
        {
            var reason = $"payload is not a valid label payload: {Summarize(action.Payload)}";
            await store.MarkDeadLetterAsync(action.Id, _dispatcherId, reason, cancellationToken);
            logger.LogWarning("outbox dead_letter {action_id}: {error}", action.Id, reason);
            return;
        }

        var repository = await db.Repositories.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == action.RepositoryId, cancellationToken);
        if (repository is null)
        {
            var reason = $"outbox action {action.Id}: repository {action.RepositoryId} no longer exists";
            await store.MarkDeadLetterAsync(action.Id, _dispatcherId, reason, cancellationToken);
            logger.LogWarning("outbox dead_letter {action_id}: {error}", action.Id, reason);
            return;
        }

        try
        {
            await client.SetIssueLabelAsync(repository.Owner, repository.Name, issueNumber.Value, label, add, cancellationToken);
            await store.MarkCompletedAsync(action.Id, _dispatcherId, cancellationToken);
            logger.LogInformation("outbox label delivered {action_id} to {repo}/{issue_number}: {verb} '{label}' (attempts {attempts})",
                action.Id, repository.FullName, issueNumber, add ? "add" : "remove", label, action.Attempts);
        }
        catch (GitHubDeliveryException exception)
        {
            await ClassifyDeliveryFailureAsync(store, action, exception, "label", cancellationToken);
        }
    }

    private static (long? IssueNumber, string? Label, bool Add) ParseLabelPayload(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var issueNumber = document.RootElement.GetProperty("issue_number").GetInt64();
            var label = document.RootElement.GetProperty("label").GetString();
            var add = document.RootElement.GetProperty("add").GetBoolean();
            return issueNumber > 0 && !string.IsNullOrWhiteSpace(label) ? (issueNumber, label, add) : (null, null, false);
        }
        catch (JsonException)
        {
            return (null, null, false);
        }
        catch (FormatException)
        {
            return (null, null, false);
        }
    }

    /// <summary>The shared permanent/transient split for the idempotent GitHub writes: an ambiguous
    /// failure (no HTTP status) retries; a permanent status dead-letters; otherwise backoff retry.</summary>
    private async Task ClassifyDeliveryFailureAsync(OutboxActionStore store, OutboxAction action,
        GitHubDeliveryException exception, string kind, CancellationToken cancellationToken)
    {
        if (exception.Status is null)
        {
            var error = $"ambiguous: {exception.Message}";
            await store.MarkTransientFailureAsync(action.Id, _dispatcherId, error, Backoff(action.Attempts), cancellationToken);
            logger.LogWarning("outbox ambiguous {kind} delivery {action_id} (attempt {attempts}): {error}", kind, action.Id, action.Attempts, error);
        }
        else if (IsPermanent(exception))
        {
            await store.MarkDeadLetterAsync(action.Id, _dispatcherId, exception.Message, cancellationToken);
            logger.LogWarning("outbox dead_letter {action_id} after {attempts} attempts: {error}", action.Id, action.Attempts, exception.Message);
        }
        else
        {
            var delay = Backoff(action.Attempts);
            if (exception.RetryAfterSeconds is { } retryAfter)
                delay = TimeSpan.FromTicks(Math.Min(MaxInterval.Ticks, Math.Max(delay.Ticks, TimeSpan.FromSeconds(retryAfter).Ticks)));
            await store.MarkTransientFailureAsync(action.Id, _dispatcherId, exception.Message, delay, cancellationToken);
            logger.LogWarning("outbox transient {kind} delivery failure {action_id} (attempt {attempts}) retry in {delay}: {error}",
                kind, action.Id, action.Attempts, delay, exception.Message);
        }
    }

    private static (long? IssueNumber, string? Target) ParseAiLabelPayload(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var issueNumber = document.RootElement.GetProperty("issue_number").GetInt64();
            var labelElement = document.RootElement.GetProperty("label");
            if (issueNumber <= 0)
                return (null, null);
            return labelElement.ValueKind == JsonValueKind.Null
                ? (issueNumber, null)
                : labelElement.ValueKind == JsonValueKind.String
                    ? (issueNumber, labelElement.GetString())
                    : (null, null);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException)
        {
            return (null, null);
        }
    }

    /// <summary>Permanent: 401, 404, 422, and any 403 that is not a rate limit (remaining != 0).</summary>
    private static bool IsPermanent(GitHubDeliveryException exception) =>
        exception.Status is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity
        || (exception.Status == HttpStatusCode.Forbidden && exception.RateLimitRemaining != 0);

    /// <summary>attempt n delays min(1h, 8s·2^(n−1)) — 8s, 16s, …, 1h cap, inside the retry window.</summary>
    internal static TimeSpan Backoff(int attempts)
    {
        var ticks = BaseBackoff.Ticks * (long)Math.Pow(2, Math.Max(1, attempts) - 1);
        return TimeSpan.FromTicks(Math.Min(MaxInterval.Ticks, ticks));
    }

    internal static string Marker(string idempotencyKey) => $"<!-- pi-agent:outbox:{idempotencyKey} -->";

    /// <summary>jsonb payload contract (ruling 8): { "issue_number": long, "text": string }.</summary>
    private static (long IssueNumber, string Text)? ParsePayload(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var issueNumber = document.RootElement.GetProperty("issue_number").GetInt64();
            var text = document.RootElement.GetProperty("text").GetString();
            return issueNumber > 0 && !string.IsNullOrWhiteSpace(text) ? (issueNumber, text) : null;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string Summarize(string payload)
    {
        var oneLine = payload.Replace("\n", " ").Replace("\r", "");
        return oneLine.Length <= 120 ? oneLine : oneLine[..117] + "...";
    }

    private async Task RefreshStateAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var counts = await db.OutboxActions
            .GroupBy(a => a.Status)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        outboxState.Pending = 0;
        outboxState.Failed = 0;
        outboxState.DeadLetter = 0;
        foreach (var count in counts)
        {
            if (count.Key == OutboxStatuses.Pending) outboxState.Pending = count.Count;
            if (count.Key == OutboxStatuses.Failed) outboxState.Failed = count.Count;
            if (count.Key == OutboxStatuses.DeadLetter) outboxState.DeadLetter = count.Count;
        }
    }
}
