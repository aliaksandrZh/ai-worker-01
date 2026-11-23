using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiAgent.Poller;

/// <summary>Shared JSON shape of the two `sync_cursor.cursor_value` payloads (opaque strings):
/// camelCase, tolerant readers — malformed/absent parses to null and the stream bootstraps again
/// (fingerprint dedupe keeps re-sync idempotent).</summary>
public static class PollerCursorJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// Cursor for the timestamp streams (issues, pulls, issue_comments, pr_review_comments):
/// `position` = newest `updated_at_github` durably saved (the boundary of guaranteed-processed), plus
/// an optional etag for fixed-shape list requests. Incremental passes re-query at `position − overlap`.
/// </summary>
public sealed record TimestampCursor(DateTime? Position, string? Etag = null)
{
    public string Serialize() => JsonSerializer.Serialize(this, PollerCursorJson.Options);

    public static TimestampCursor? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<TimestampCursor>(raw, PollerCursorJson.Options);
            return parsed is null ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Cursor shard for one work item's `issues/{n}/timeline`: `last_visit`
/// = the `updated_at_github` observed at the last successfully-processed visit — the visit gate is
/// `item.UpdatedAtGithub > last_visit` (closure/cross-reference/review-request/connected-disconnected
/// bumps revisit the item); `position` = the highest timeline event id durably saved (timeline lists
/// ascend by event id, so a page whose max id ≤ position is client-side-skipped). An unresolved link
/// event holds back BOTH values by not writing the entry (next cycle revisits; dedupe keeps it idempotent).</summary>
public sealed record TimelineItemCursor(DateTime LastVisit, long? Position);

/// <summary>Cursor for the `timeline` stream: per-work-item entries keyed by number.</summary>
public sealed record TimelineCursor(IReadOnlyDictionary<long, TimelineItemCursor> Items)
{
    public string Serialize() => JsonSerializer.Serialize(this, PollerCursorJson.Options);

    public static TimelineCursor? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<TimelineCursor>(raw, PollerCursorJson.Options);
            return parsed is null ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Cursor for the `pr_reviews` stream: per-PR etags from conditional
/// `pulls/{n}/reviews` requests; 304s skip without consuming the primary limit (GitHub-side).</summary>
public sealed record PrReviewCursor(IReadOnlyDictionary<long, string> Etags)
{
    public string Serialize() => JsonSerializer.Serialize(this, PollerCursorJson.Options);

    public static PrReviewCursor? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<PrReviewCursor>(raw, PollerCursorJson.Options);
            return parsed is null ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
