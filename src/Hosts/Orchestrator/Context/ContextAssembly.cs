using System.Security.Cryptography;
using System.Text;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Orchestration;

namespace PiAgent.Orchestrator.Context;

/// <summary>
/// The assembled, budget-checked Task input for one Run — frozen as the Run's immutable
/// RunContextSnapshot before dispatch (02 §14a, decision 63). <see cref="Content"/> is the serialized
/// input (the context package, v2 shape) the Runtime API serves to the Bridge over `/input`;
/// <see cref="IncludedSources"/>/<see cref="ExcludedSources"/> record every considered source element.
/// </summary>
public sealed record ContextAssembly(string Content, string IncludedSources, string ExcludedSources)
{
    /// <summary>sha256 hex over <see cref="Content"/> — the snapshot's content_hash (02 §14a).</summary>
    public string ContentHash() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Content))).ToLowerInvariant();
}

/// <summary>Outcome of one assembly pass. `TooLarge` = mandatory context alone cannot fit the
/// profile's budgets — the Run is never started and the Task fails with `context_too_large`
/// (decision 63; no silent truncation).</summary>
public sealed record ContextAssemblyResult(ContextAssembly? Assembled, string? TooLargeDetail)
{
    public bool TooLarge => Assembled is null;

    /// <summary>Canonical task failure code (04 §7) — always `context_too_large` when <see cref="TooLarge"/>.</summary>
    public const string FailureCode = "context_too_large";
}

/// <summary>Freezes the per-Run immutable snapshot row (one per Run, 02 §23) from an assembly.
/// Called by the dispatcher AFTER RunPrepare created the Run row and BEFORE POST /start — the Runtime
/// API requires the frozen snapshot to be persisted and visible when it answers `/start` and `/input`.
/// Snapshots are never modified: the caller deletes pending Run rows (claim release / lease sweep)
/// which cascades the row away; an existing row with a DIFFERENT hash for the same Run is an ordering
/// bug and aborts the dispatch instead of silently rewriting immutable state.</summary>
public static class ContextAssemblyFreezable
{
    public static ContextAssembly FreezeOrThrow(AppDbContext db, RunPreparation preparation, ContextAssembly assembly)
    {
        var existing = db.RunContextSnapshots.SingleOrDefault(s => s.RunId == preparation.Run.Id);
        var hash = assembly.ContentHash();
        var now = DateTime.UtcNow;
        if (existing is not null)
        {
            if (existing.ContentHash == hash && existing.Content == assembly.Content)
                return assembly; // idempotent re-entry (same Run, same bytes) — immutability preserved
            throw new InvalidOperationException(
                $"run_context_snapshot for run {preparation.Run.Id} already exists with a different hash — " +
                "snapshots are immutable; this is an assembler/dispatcher ordering bug");
        }
        db.RunContextSnapshots.Add(new RunContextSnapshot
        {
            Id = Guid.NewGuid(),
            RunId = preparation.Run.Id,
            TaskId = preparation.Task.Id,
            ContentHash = hash,
            IncludedSources = assembly.IncludedSources,
            ExcludedSources = assembly.ExcludedSources,
            Content = assembly.Content,
            CreatedAt = now,
        });
        return assembly;
    }
}
