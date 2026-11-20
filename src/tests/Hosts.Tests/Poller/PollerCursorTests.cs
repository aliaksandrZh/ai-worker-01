using PiAgent.Poller;
using Xunit;

namespace PiAgent.Hosts.Tests.Poller;

/// <summary>
/// Sync-cursor serialization for sync_cursor.cursor_value (02 §8 opaque string): timestamp cursors
/// (position + optional etag) and per-PR etag maps, camelCase JSON, malformed/absent → null (bootstrap;
/// fingerprint dedupe makes re-sync idempotent).
/// </summary>
public class PollerCursorTests
{
    [Fact]
    public void TimestampCursor_RoundTrip()
    {
        var at = new DateTime(2026, 11, 20, 12, 30, 0, DateTimeKind.Utc);
        var cursor = new TimestampCursor(at, "abc123");
        var parsed = TimestampCursor.TryParse(cursor.Serialize());
        Assert.NotNull(parsed);
        Assert.Equal(at, parsed!.Position);
        Assert.Equal("abc123", parsed!.Etag);
    }

    [Fact]
    public void TimestampCursor_NullEtag_RoundTrip()
    {
        var at = new DateTime(2026, 11, 20, 12, 30, 0, DateTimeKind.Utc);
        var parsed = TimestampCursor.TryParse(new TimestampCursor(at).Serialize());
        Assert.NotNull(parsed);
        Assert.Equal(at, parsed!.Position);
        Assert.Null(parsed!.Etag);
    }

    [Fact]
    public void TimestampCursor_MalformedOrEmpty_IsNull()
    {
        Assert.Null(TimestampCursor.TryParse(null));
        Assert.Null(TimestampCursor.TryParse(""));
        Assert.Null(TimestampCursor.TryParse("{{{not json"));
        Assert.Null(TimestampCursor.TryParse("\"a string\""));
    }

    [Fact]
    public void TimestampCursor_RoundTripsToUtc()
    {
        // positions are UTC; a deserialize→serialize cycle must not skew
        var at = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var parsed = TimestampCursor.TryParse(new TimestampCursor(at).Serialize())!;
        Assert.Equal(TimeSpan.Zero, parsed.Position!.Value - at);
    }

    [Fact]
    public void PrReviewCursor_RoundTrip_LongKeys()
    {
        var cursor = new PrReviewCursor(new Dictionary<long, string> { [1714234] = "w5", [9] = "w9" });
        var parsed = PrReviewCursor.TryParse(cursor.Serialize());
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Etags.Count);
        Assert.Equal("w5", parsed!.Etags[1714234]);
        Assert.Equal("w9", parsed!.Etags[9]);
    }

    [Fact]
    public void PrReviewCursor_Malformed_IsNull()
    {
        Assert.Null(PrReviewCursor.TryParse("banana"));
        Assert.Null(PrReviewCursor.TryParse(null));
    }
}
