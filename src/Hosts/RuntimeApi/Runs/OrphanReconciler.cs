using PiAgent.Platform.Contracts;
using PiAgent.RuntimeApi.Provisioning;
using PiAgent.RuntimeApi.Api;
using PiAgent.RuntimeApi.Runs;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Workers;

namespace PiAgent.RuntimeApi.Runs;

/// <summary>
/// The decision-77 orphan reconciliation (ticket 13): the Host Monitor reports the top-level workflow
/// directories under the managed root plus which bind mounts the observed containers use; this
/// reconciler resolves them against PostgreSQL. A directory becomes a CANDIDATE when its Workflow row
/// is gone (or the storage is already marked deleted) — candidates persist ≥ `orphan_grace_period`,
/// are then QUARANTINED (physically renamed `&lt;name&gt;.orphan-quarantine` under the managed root),
/// and only after `orphan_quarantine_period` are physically deleted. Deletion never runs while
/// PostgreSQL is unavailable (this row can only be written when it is) or while the Host Monitor
/// still observed a container bind covering the tree; candidate/quarantine bookkeeping survives
/// restarts through the `orphan_candidate` table.
/// The Runtime API performs the rename/delete through the SAME writable managed-root mount that
/// provisions trees (`Storage__ManagedRoot`); the Orchestrator/Poller never touch host paths (the
/// decision-62 boundary stays intact — every path fact enters through Host-Monitor telemetry).
/// </summary>
public sealed class OrphanReconciler(
    IServiceScopeFactory scopeFactory,
    RuntimeLimits limits,
    ILogger<OrphanReconciler> logger) : IWorkerCycle
{
    /// <summary>Physical rename applied at quarantine (`<name>.orphan-quarantine`).</summary>
    internal const string QuarantineSuffix = ".orphan-quarantine";

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var observation = await db.MonitorObservations.AsNoTracking().OrderByDescending(o => o.UpdatedAt).FirstOrDefaultAsync(cancellationToken);
        if (observation?.Storage is not { } storageJson || DateTime.UtcNow - observation.ObservedAt > TimeoutSweeper.TelemetryFreshness)
            return; // no fresh observation: no orphan decision this cycle (fail-safe)

        MonitorStorageTelemetry storage;
        try
        {
            storage = JsonSerializer.Deserialize<MonitorStorageTelemetry>(storageJson, CallbackHandler.PayloadJson)!;
        }
        catch (JsonException)
        {
            return; // a corrupted observation never authorizes action; the next cycle replaces it
        }
        if (storage?.Directories is null)
            return;

        // The tree-level deletion guard (decision 77): bind prefixes observed by the Host Monitor.
        var boundPrefixes = (storage.Directories.Length == 0 ? [] : await ContainerBindsAsync(db, observation, cancellationToken));

        var now = DateTime.UtcNow;
        var knownWorkflows = storage.Directories
            .Select(d => Guid.TryParse(d.Name, out var id) ? id : (Guid?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
        var existingIds = await db.Workflows.AsNoTracking()
            .Where(w => knownWorkflows.Contains(w.Id))
            .Select(w => w.Id)
            .ToListAsync(cancellationToken);
        var existingSet = existingIds.ToHashSet();

        var reported = storage.Directories.ToDictionary(d => d.Name, d => d.ModifiedAtUtc);

        // 1. Existing candidates advance through their lifecycle (grace → quarantine → deletion).
        var candidates = await db.OrphanCandidates.Where(c => c.Status != OrphanCandidateStatuses.Deleted)
            .ToListAsync(cancellationToken);
        foreach (var candidate in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            var (stillExists, stillBound) = TreeState(reported, boundPrefixes, candidate.Path);
            if (!stillExists)
            {
                // The tree vanished (operator action or already handled): the record closes without a delete.
                candidate.Status = OrphanCandidateStatuses.Deleted;
                candidate.DeletedAt = candidate.DeletedAt ?? now;
                candidate.UpdatedAt = now;
                continue;
            }
            if (stillBound)
            {
                logger.LogWarning("orphan {path} is still bound by a container — deletion/quarantine deferred (decision 77)", candidate.Path);
                candidate.UpdatedAt = now;
                continue;
            }

            if (candidate.Status == OrphanCandidateStatuses.Candidate)
            {
                if (now - candidate.FirstSeenAt >= limits.OrphanGracePeriod)
                {
                    var quarantinePath = $"{candidate.Path}{QuarantineSuffix}";
                    if (!TryQuarantine(candidate.Path, quarantinePath, out var error))
                    {
                        logger.LogError("orphan {path} quarantine failed: {error}", candidate.Path, error);
                        continue;
                    }
                    candidate.Status = OrphanCandidateStatuses.Quarantined;
                    candidate.QuarantinedAt = now;
                    candidate.Path = quarantinePath;
                    candidate.DirectoryName = $"{candidate.DirectoryName}{QuarantineSuffix}";
                    candidate.UpdatedAt = now;
                    await db.SaveChangesAsync(cancellationToken);
                    logger.LogWarning("orphan {path} quarantined for {period} before deletion", candidate.Path, limits.OrphanQuarantinePeriod);
                }
            }
            else // quarantined
            {
                if (now - (candidate.QuarantinedAt ?? candidate.FirstSeenAt) >= limits.OrphanQuarantinePeriod)
                {
                    if (!TryDelete(candidate.Path, out var error))
                    {
                        logger.LogError("orphan {path} deletion failed: {error}", candidate.Path, error);
                        continue;
                    }
                    candidate.Status = OrphanCandidateStatuses.Deleted;
                    candidate.DeletedAt = now;
                    candidate.UpdatedAt = now;
                    logger.LogWarning("orphan {path} deleted after quarantine", candidate.Path);
                }
            }
        }

        // 2. Fresh directories: register new candidates when the DB has no workflow for them.
        foreach (var directory in storage.Directories)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            // already tracked under its name or its quarantined rename — never duplicate the row
            if (candidates.Any(c => c.DirectoryName == directory.Name
                || c.DirectoryName == directory.Name + QuarantineSuffix))
                continue;
            if (!Guid.TryParse(directory.Name, out var workflowId))
            {
                logger.LogWarning("unrecognized directory under the managed root: {name}", directory.Name);
                continue; // not a decision-52 workflow tree shape; operator-visible log only
            }
            if (existingSet.Contains(workflowId))
                continue; // live workflow — the tree is legitimately present

            var (path, bound) = await TreePathAsync(storage.Root, workflowId, boundPrefixes);
            if (bound)
            {
                logger.LogWarning("workflow {workflowId} has no DB row but a container binds its tree — recorded, never deleted while bound", workflowId);
            }
            db.OrphanCandidates.Add(new OrphanCandidate
            {
                Id = Guid.NewGuid(),
                DirectoryName = directory.Name,
                Path = path,
                Status = OrphanCandidateStatuses.Candidate,
                FirstSeenAt = now,
                Details = bound
                    ? "workflow row missing; a container bind covers the tree"
                    : "workflow row missing",
                UpdatedAt = now,
            });
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Bind prefixes covering managed storage, from the latest observation's container rows.</summary>
    private static async Task<List<string>> ContainerBindsAsync(
        AppDbContext db, MonitorObservation observation, CancellationToken cancellationToken)
    {
        var prefixes = new List<string>();
        if (observation.Containers is not { } containersJson)
            return prefixes;
        try
        {
            using var document = JsonDocument.Parse(containersJson);
            foreach (var container in document.RootElement.EnumerateArray())
            {
                if (container.TryGetProperty("binds", out var binds) && binds.ValueKind == JsonValueKind.Array)
                    foreach (var bind in binds.EnumerateArray())
                    {
                        var value = bind.GetString();
                        if (!string.IsNullOrEmpty(value))
                            prefixes.Add(value.Split(':')[0]); // the HOST side of `host:container[:mode]`
                    }
            }
        }
        catch (JsonException)
        {
            // no binds → the guards below only fail closed
        }
        await Task.CompletedTask;
        return prefixes;
    }

    private static (bool Exists, bool Bound) TreeState(
        Dictionary<string, DateTime> reported, List<string> boundPrefixes, string path)
    {
        var bound = boundPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));
        var name = Path.GetFileName(path);
        var exists = reported.ContainsKey(name) || Directory.Exists(path);
        return (exists, bound);
    }

    private static Task<(string Path, bool Bound)> TreePathAsync(
        string root, Guid workflowId, List<string> boundPrefixes)
    {
        var path = Path.Combine(root, workflowId.ToString());
        var bound = boundPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));
        return Task.FromResult((path, bound));
    }

    private bool TryQuarantine(string from, string to, out string? error)
    {
        try
        {
            Directory.Move(from, to);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
    }

    private bool TryDelete(string path, out string? error)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
    }
}
