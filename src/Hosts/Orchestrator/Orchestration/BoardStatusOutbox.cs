using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Config;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>
/// The enqueue-side of the AI-driven board status moves (projects.yaml `ai_statuses`): when a run is
/// handed off (AI Processing) the item's board card moves to `processing`, and when an implement
/// Task succeeds (the pull request is up) it moves to `review`. Resolution happens here — the
/// Outbox Worker has no DB — from the synced ProjectItems: the work item's issue number matches a
/// synced board item of a configured `owner/number` board; the field and option NAMES travel in the
/// payload and the Outbox Worker resolves their GraphQL ids at delivery time (the Poller does not
/// sync field option ids). Everything is best-effort: an item not on a configured board, a board
/// without the named column, or a missing board config silently skips — the AI state labels remain
/// the authoritative lifecycle signal.
/// </summary>
public static class BoardStatusOutbox
{
    /// <summary>The state the board moves to; `null` targets mean "do not move for this state".</summary>
    public enum State { Processing, Review, Testing }

    /// <summary>The item's current board column (exact-case), or null when the item is not on a
    /// configured board. Used by the manual-/code gate — a null is "not locked" (fail open).</summary>
    public static async Task<(string StatusField, string Status, ProjectStatusConfig Config)?> TryGetBoardStatusAsync(
        AppDbContext db, ConfigurationState configurationState,
        Guid repositoryId, long issueNumber, CancellationToken cancellationToken)
    {
        var rows = await ResolveAsync(db, configurationState, repositoryId, issueNumber, cancellationToken);
        if (rows is not { } resolved)
            return null;
        var (board, projectItem) = resolved;
        var status = ReadSingleSelect(projectItem.FieldValues, board.StatusField);
        return status is null ? null : (board.StatusField, status, board.Config);
    }

    /// <summary>Resolves the item's configured board (at most one — the first match wins) and
    /// enqueues the move; a no-op when the item is not on a configured board or the state is not
    /// configured for it. Safe inside the caller's transaction (outbox semantics).</summary>
    public static async Task MoveAsync(
        AppDbContext db, ConfigurationState configurationState,
        Guid repositoryId, Guid workItemId, long issueNumber, Guid? taskId, State state,
        CancellationToken cancellationToken)
    {
        if (await ResolveAsync(db, configurationState, repositoryId, issueNumber, cancellationToken)
            is not { } resolved)
            return; // not on a configured board (or no board config at all): nothing to move
        var (board, projectItem) = resolved;

        var target = state switch
        {
            State.Processing => board.Config.Processing,
            State.Review => board.Config.Review,
            State.Testing => board.Config.Testing,
            _ => null,
        };
        if (string.IsNullOrEmpty(target))
            return; // the board does not drive this state

        await new OutboxActionStore(db).EnqueueProjectStatusAsync(
            new ProjectStatusEnqueueRequest(repositoryId, workItemId, taskId, issueNumber,
                board.Owner, board.Number, projectItem.ItemId, projectItem.ItemNodeId, board.StatusField, target),
            cancellationToken);
    }

    private sealed record BoardRow(Guid SyncId, string Owner, int Number, string StatusField,
        ProjectStatusConfig Config);

    private sealed record Resolved(BoardRow Board, ProjectItem Item);

    /// <summary>The first configured board whose synced ProjectItem row carries this repository +
    /// issue number, together with that item row; null when none.</summary>
    private static async Task<Resolved?> ResolveAsync(
        AppDbContext db, ConfigurationState configurationState,
        Guid repositoryId, long issueNumber, CancellationToken cancellationToken)
    {
        if (configurationState.ProjectStatusConfigs.Count == 0)
            return null;

        var rows = await db.ProjectItems.AsNoTracking()
            .Where(i => i.RepositoryId == repositoryId && i.Number == (int)issueNumber)
            .Join(db.ProjectSyncs.AsNoTracking(),
                i => i.ProjectSyncId, s => s.Id,
                (i, s) => new { Item = i, s.Owner, s.ProjectNumber })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
            return null;

        foreach (var config in configurationState.ProjectStatusConfigs)
        {
            var row = rows.FirstOrDefault(r => r.Owner == config.Owner && r.ProjectNumber == config.Number);
            if (row is not null)
                return new Resolved(
                    new BoardRow(row.Item.ProjectSyncId, config.Owner, config.Number, config.StatusField, config),
                    row.Item);
        }
        return null;
    }

    /// <summary>The current value of one single-select field from the stored
    /// `project_item.field_values` jsonb; null when the field is absent or unreadable.</summary>
    private static string? ReadSingleSelect(string fieldValuesJson, string fieldName)
    {
        try
        {
            using var document = JsonDocument.Parse(fieldValuesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var field in document.RootElement.EnumerateArray())
                if (field.ValueKind == JsonValueKind.Object
                    && field.TryGetProperty("name", out var name)
                    && string.Equals(name.GetString(), fieldName, StringComparison.Ordinal)
                    && field.TryGetProperty("value", out var value)
                    && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
