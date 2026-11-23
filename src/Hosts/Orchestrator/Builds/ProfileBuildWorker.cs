using PiAgent.Platform.Contracts;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Config;
using PiAgent.Platform.Workers;

namespace PiAgent.Orchestrator.Builds;

/// <summary>
/// Decision 81 orchestration side: every AgentProfileVersion is built asynchronously through the Build
/// Worker. Per tick (all steps idempotent — the restart reconciler is this same cycle over durable rows):
/// <list type="bullet">
/// <item>Open BuildJobs: a version in `draft` (or `building` without a job — e.g. recovery) gets a durable
/// BuildJob; the `buildId` is persisted BEFORE dispatch, and `draft → building` flips in the same write.
/// The Config Loader never builds or dispatches (decision 81); this cycle is the only dispatcher.</item>
/// <item>Dispatch due `pending` BuildJobs: `POST /internal/builds` (idempotent by buildId on the worker) →
/// `polling`. Transport/5xx/429 = transient: exponential backoff, the version STAYS `building` — network
/// failures and worker unavailability are never build failures.</item>
/// <item>Poll `polling` BuildJobs: `GET /internal/builds/{buildId}`. A validated `succeeded` result
/// (local image tag, image ID/digest, build-log reference) completes the job, records `container_image`/
/// `image_digest` on the version and sets it `ready`; a `failed` result (real Docker build failure) is
/// terminal: the job fails and the version becomes `build_failed`. 404 after having been accepted means
/// the Worker lost its local state — the job returns to `pending` for a same-buildId redispatch.</item>
/// </list>
/// The Worker never touches PostgreSQL; the Orchestrator performs every version transition itself.
/// </summary>
public sealed class ProfileBuildWorker(
    IServiceScopeFactory scopeFactory,
    ConfigurationState configurationState,
    BuildWorkerApiClient client,
    IOptions<BuildWorkerOptions> options,
    BuildMetrics metrics,
    ILogger<ProfileBuildWorker> logger) : IWorkerCycle
{
    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        // cold-start gate (01 §13): no build dispatch before configuration synchronization succeeded
        if (!configurationState.Synchronized)
            return;

        await OpenBuildJobsAsync(cancellationToken);
        await ProcessDueJobsAsync(cancellationToken);
        await RefreshGaugesAsync(cancellationToken);
    }

    /// <summary>One transaction keeps BuildJob creation and the `building` flip atomic (decision 81:
    /// the buildId is persisted before dispatch; a crash after commit resumes from the durable row).</summary>
    private async Task OpenBuildJobsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var targets = await db.AgentProfileVersions
            .Where(v => v.State == ProfileVersionStates.Draft || v.State == ProfileVersionStates.Building)
            .Where(v => !db.BuildJobs.Any(j => j.AgentProfileVersionId == v.Id))
            .Join(db.AgentProfiles, v => v.AgentProfileId, p => p.Id, (v, p) => new { Version = v, Profile = p })
            .ToListAsync(cancellationToken);
        if (targets.Count == 0)
            return;

        var opened = new List<Guid>();
        foreach (var target in targets)
        {
            var request = new BuildDispatchRequest(
                Guid.NewGuid(), // the buildId: generated and persisted before any dispatch call
                target.Profile.Name,
                target.Version.Version,
                target.Version.SourceCommitSha,
                target.Version.ConfigSnapshot,
                new BuildContextReference(target.Version.SourceDirectory));
            var job = new BuildJob
            {
                Id = request.BuildId,
                AgentProfileVersionId = target.Version.Id,
                DispatchPayload = JsonSerializer.Serialize(request, BuildStatuses.PayloadJson),
                State = BuildJobStates.Pending,
                Attempts = 0,
                NextRetryAt = null,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.BuildJobs.Add(job);
            // recovery case: a version already `building` (previous process died pre-DB-write ordering)
            if (target.Version.State == ProfileVersionStates.Draft)
                target.Version.State = ProfileVersionStates.Building;
            opened.Add(target.Version.Id);
        }
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("opened {count} BuildJob(s) for profile version(s) {versions}", targets.Count, string.Join(", ", opened));
    }

    private async Task ProcessDueJobsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        var due = await db.BuildJobs
            .Where(j => (j.State == BuildJobStates.Pending && (j.NextRetryAt == null || j.NextRetryAt <= now))
                || j.State == BuildJobStates.Polling)
            .OrderBy(j => j.NextRetryAt ?? j.CreatedAt)
            .Take(options.Value.BatchLimit)
            .Join(db.AgentProfileVersions, j => j.AgentProfileVersionId, v => v.Id, (j, v) => new { Job = j, Version = v })
            .Join(db.AgentProfiles, jv => jv.Version.AgentProfileId, p => p.Id, (jv, p) => new { jv.Job, jv.Version, Profile = p })
            .ToListAsync(cancellationToken);

        foreach (var entry in due)
        {
            if (entry.Job.State == BuildJobStates.Pending)
                await DispatchJobAsync(db, entry.Job, entry.Version, entry.Profile, cancellationToken);
            else if (entry.Job.State == BuildJobStates.Polling)
                await PollJobAsync(db, entry.Job, entry.Version, entry.Profile, cancellationToken);
        }
    }

    private async Task DispatchJobAsync(
        AppDbContext db, BuildJob job, AgentProfileVersion version, AgentProfile profile, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<BuildDispatchRequest>(job.DispatchPayload, BuildStatuses.PayloadJson);
        if (request is null)
        {
            // permanently malformed lineage: never a build failure — the version stays `building` under retry
            await TransientAsync("dispatch payload is unreadable", db, job, cancellationToken, permanent: true);
            return;
        }

        HttpResponseMessage response;
        try
        {
            response = await client.DispatchAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            await TransientAsync($"dispatch transport failure: {Truncate(exception.Message)}", db, job, cancellationToken);
            return;
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                job.State = BuildJobStates.Polling;
                job.Attempts = 0; // successful contact: consecutive-failure counter resets
                job.NextRetryAt = null;
                job.LastError = null;
                job.UpdatedAt = DateTime.UtcNow;
                metrics.BuildsDispatchedTotal++;
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("build {buildId} for {profile} v{version} dispatched (202 accepted)",
                    job.Id, profile.Name, version.Version);
                return;
            }

            if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                await TransientAsync($"dispatch answered {response.StatusCode:D}", db, job, cancellationToken);
                return;
            }

            // 4xx: the Worker rejected the dispatch (auth misconfiguration, malformed payload). Never a
            // build failure (the version keeps `building`); back off and surface it for operators.
            await TransientAsync($"dispatch rejected ({response.StatusCode:D})", db, job, cancellationToken, permanent: true);
        }
    }

    private async Task PollJobAsync(
        AppDbContext db, BuildJob job, AgentProfileVersion version, AgentProfile profile, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.PollAsync(job.Id, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            await TransientAsync($"poll transport failure: {Truncate(exception.Message)}", db, job, cancellationToken);
            return;
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // the Worker answered 202 here once, so a 404 now means it lost its local durable state:
                // return to `pending` and redispatch the SAME buildId (worker-side idempotency rebuilds it).
                job.State = BuildJobStates.Pending;
                await TransientAsync("poll answered 404 — redispatching with the same buildId", db, job, cancellationToken);
                return;
            }
            if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                await TransientAsync($"poll answered {response.StatusCode:D}", db, job, cancellationToken);
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                await TransientAsync($"poll rejected ({response.StatusCode:D})", db, job, cancellationToken, permanent: true);
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            BuildResponse? status;
            try
            {
                status = JsonSerializer.Deserialize<BuildResponse>(body, BuildStatuses.PayloadJson);
            }
            catch (JsonException exception)
            {
                await TransientAsync($"poll body unreadable: {Truncate(exception.Message)}", db, job, cancellationToken);
                return;
            }

            switch (status?.State)
            {
                case BuildStates.Queued:
                case BuildStates.Building:
                    job.Attempts = 0; // successful contact
                    job.NextRetryAt = null;
                    job.LastError = null;
                    job.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                case BuildStates.Succeeded:
                    if (ValidateSuccess(status, profile, version) is { } violation)
                    {
                        metrics.InvalidPollResultsTotal++;
                        await TransientAsync($"succeeded result failed validation: {violation}", db, job, cancellationToken);
                        return;
                    }
                    await CompleteJobAsync(db, job, version, status, cancellationToken);
                    return;
                case BuildStates.Failed:
                    await FailJobAsync(db, job, version, status.ErrorCode, status.ErrorMessage ?? "the Build Worker reported a failed build result", cancellationToken);
                    return;
                default:
                    metrics.InvalidPollResultsTotal++;
                    await TransientAsync(status is null ? "poll body missing" : $"unknown build state '{status.State}'", db, job, cancellationToken);
                    return;
            }
        }
    }

    /// <summary>Decision 81: the Orchestrator validates the polled result before any version transition —
    /// the local image tag must match the pinned convention and ID/digest/log reference must be present.</summary>
    private string? ValidateSuccess(BuildResponse status, AgentProfile profile, AgentProfileVersion version)
    {
        var expectedTag = BuildStatuses.ImageTagFor(profile.Name, version.Version);
        if (!string.Equals(status.ImageTag, expectedTag, StringComparison.Ordinal))
            return $"image tag '{status.ImageTag}' does not match the expected '{expectedTag}'";
        if (string.IsNullOrWhiteSpace(status.ImageId))
            return "the result carries no image id";
        if (string.IsNullOrWhiteSpace(status.ImageDigest))
            return "the result carries no image digest";
        if (string.IsNullOrWhiteSpace(status.BuildLogReference))
            return "the result carries no build log reference";
        return null;
    }

    private async Task CompleteJobAsync(
        AppDbContext db, BuildJob job, AgentProfileVersion version, BuildResponse status, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        job.State = BuildJobStates.Completed;
        job.ImageTag = status.ImageTag;
        job.ImageDigest = status.ImageDigest; // mirror target (02 §12b): matches AgentProfileVersion.ImageDigest
        job.BuildLogReference = status.BuildLogReference;
        job.Attempts = 0;
        job.NextRetryAt = null;
        job.LastError = null;
        job.UpdatedAt = now;
        version.ContainerImage = status.ImageTag;
        version.ImageDigest = status.ImageDigest;
        version.State = ProfileVersionStates.Ready;
        await db.SaveChangesAsync(cancellationToken);
        metrics.BuildsCompletedTotal++;
        logger.LogInformation("build {buildId} completed: {tag} ({digest}) — version {version} is ready",
            job.Id, status.ImageTag, status.ImageDigest, version.Version);
    }

    private async Task FailJobAsync(
        AppDbContext db, BuildJob job, AgentProfileVersion version, string? errorCode, string error, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        job.State = BuildJobStates.Failed;
        job.Attempts = 0;
        job.NextRetryAt = null;
        job.LastError = $"build failed{(errorCode is null ? "" : $" ({errorCode})")}: {error}";
        job.UpdatedAt = now;
        version.State = ProfileVersionStates.BuildFailed; // the ONLY terminal trigger (decisions 35/56/81)
        await db.SaveChangesAsync(cancellationToken);
        metrics.BuildsFailedTotal++;
        logger.LogWarning("build {buildId} failed — version {version} set to build_failed: {error}",
            job.Id, version.Version, job.LastError);
    }

    /// <summary>Transient bookkeeping shared by dispatch and poll: exponential backoff, the version stays
    /// `building` (network failures / worker unavailability are never `build_failed`), same buildId reused.</summary>
    private async Task TransientAsync(string error, AppDbContext db, BuildJob job, CancellationToken cancellationToken, bool permanent = false)
    {
        var now = DateTime.UtcNow;
        var settings = options.Value;
        var seconds = Math.Min(settings.InitialBackoffSeconds * Math.Pow(2, Math.Max(job.Attempts, 1) - 1), settings.MaxBackoffSeconds);
        job.Attempts++;
        job.NextRetryAt = now + TimeSpan.FromSeconds(seconds);
        job.LastError = error;
        job.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        metrics.TransientBuildErrorsTotal++;
        logger.LogWarning("build {buildId} {kind}: {error} — retry attempt {attempts} after ~{seconds}s",
            job.Id, permanent ? "permanent dispatch/poll problem" : "transient error", error, job.Attempts, seconds);
    }

    private static string Truncate(string message)
        => message.Length <= 200 ? message : message[..200] + "…";

    private async Task RefreshGaugesAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        metrics.BuildJobsPending = await db.BuildJobs.CountAsync(j => j.State == BuildJobStates.Pending, cancellationToken);
        metrics.BuildJobsPolling = await db.BuildJobs.CountAsync(j => j.State == BuildJobStates.Polling, cancellationToken);
        var oldest = await db.BuildJobs
            .Where(j => j.State == BuildJobStates.Pending)
            .OrderBy(j => j.CreatedAt)
            .Select(j => (DateTime?)j.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        metrics.OldestPendingBuildAgeSeconds = oldest is { } createdAt ? (now - createdAt).TotalSeconds : 0;
    }
}
