using System.Net;
using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Http;

namespace PiAgent.Poller;

/// <summary>SyncCursor row access for the streams (02 §8). Rows are normally created during repository
/// onboarding (ConfigSyncService, task 02); GetOrCreate makes a missing row non-fatal. Cursor values are
/// advanced only by the streams' per-page transactions ("durable save before advance", decision 53).</summary>
public static class SyncCursors
{
    private const int MaxErrorLength = 512;

    public static async Task<SyncCursor> GetOrCreateAsync(AppDbContext db, Guid repositoryId, string stream,
        CancellationToken cancellationToken)
    {
        var existing = await db.SyncCursors
            .SingleOrDefaultAsync(c => c.RepositoryId == repositoryId && c.Stream == stream, cancellationToken);
        if (existing is not null)
            return existing;

        var cursor = new SyncCursor
        {
            Id = Guid.NewGuid(),
            RepositoryId = repositoryId,
            Stream = stream,
            UpdatedAt = DateTime.UtcNow,
        };
        db.SyncCursors.Add(cursor);
        await db.SaveChangesAsync(cancellationToken);
        return cursor;
    }

    /// <summary>Records the durable-success state — called INSIDE the page transaction, before commit.</summary>
    public static void RecordSuccess(SyncCursor cursor, string? cursorValue)
    {
        cursor.CursorValue = cursorValue;
        cursor.LastSuccessAt = DateTime.UtcNow;
        cursor.LastAttemptAt = cursor.LastSuccessAt;
        cursor.LastError = null;
        cursor.UpdatedAt = cursor.LastSuccessAt!.Value;
    }

    /// <summary>A failed attempt never advances the cursor — last_attempt/last_error only (decision 53).</summary>
    public static void RecordFailure(SyncCursor cursor, string error)
    {
        cursor.LastAttemptAt = DateTime.UtcNow;
        cursor.LastError = error.Length <= MaxErrorLength ? error : error[..(MaxErrorLength - 3)] + "...";
        cursor.UpdatedAt = cursor.LastAttemptAt!.Value;
    }

    /// <summary>Persists failure bookkeeping without letting a second DB failure mask the original outcome.</summary>
    public static async Task TrySaveFailure(AppDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception)
        {
            // the outcome (result.Error) is already reported to the cycle; a second failure here
            // must not replace the original error path
        }
    }

    /// <summary>
    /// Full stream failure path: drops every pending tracked entity (the rolled-back page data),
    /// records last_attempt/last_error on a freshly loaded cursor, and persists it best-effort.
    /// Without the ChangeTracker.Clear the failed page's rows would piggyback on the failure save.
    /// </summary>
    public static async Task<StreamResult> RecordFailureAsync(AppDbContext db, Guid repositoryId, string stream,
        Exception exception, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var cursor = await GetOrCreateAsync(db, repositoryId, stream, cancellationToken);
        RecordFailure(cursor, exception.Message);
        await TrySaveFailure(db, cancellationToken);
        // HTTP status travels to the cycle so a revoked/expired PAT (401) can surface as the proper
        // decision-65 system configuration error instead of a generic stream-failure warning.
        var status = exception is GitHubReadException read ? read.Status : null;
        return new StreamResult(StreamResultKind.Failed, exception.Message, status);
    }
}
