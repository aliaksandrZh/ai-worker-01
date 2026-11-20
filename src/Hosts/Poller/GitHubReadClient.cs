using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PiAgent.Poller;

/// <summary>One GitHub REST list page (decision 53): parsed items, `Link` rel="next" target, and the
/// rate-limit/conditional-response headers. A 304 (If-None-Match hit) yields NotModified=true with no
/// items — GitHub reports it before counting against the primary limit.</summary>
public sealed record GitHubPage(
    bool NotModified,
    IReadOnlyList<JsonElement> Items,
    Uri? NextPageUri,
    string? Etag,
    int? RateLimitRemaining,
    DateTimeOffset? RateLimitReset,
    int? RetryAfterSeconds);

/// <summary>
/// GitHub REST reads the Poller needs (decisions 4/53): list endpoints with page size 100, `Link`
/// header paging, `If-None-Match` conditional requests, and `Retry-After`/`X-RateLimit-Reset`
/// extraction. Header discipline mirrors the Outbox worker's GitHubWriteClient: Bearer PAT, GitHub
/// JSON accept, own user agent. Exception messages carry path, status, and response detail — with the
/// PAT redacted even from a (hostile) response echo.
/// </summary>
public sealed class GitHubReadClient(HttpClient httpClient, string pat)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient = Initialize(httpClient, pat);
    private readonly string _pat = string.IsNullOrEmpty(pat) ? "<empty-pat>" : pat;

    private static HttpClient Initialize(HttpClient httpClient, string pat)
    {
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("pi-agent-poller");
        return httpClient;
    }

    public async Task<GitHubPage> GetPageAsync(string relativePath, string? etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        if (!string.IsNullOrEmpty(etag))
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{etag}\""));

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified)
                return new GitHubPage(true, [], NextPage(response), null, Remaining(response), ResetAt(response),
                    RetryAfter(response));
            if (!response.IsSuccessStatusCode)
            {
                var remaining = Remaining(response);
                throw await FailureAsync(relativePath, response, cancellationToken, remaining,
                    ResetAt(response), RetryAfter(response));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var items = new List<JsonElement>();
            foreach (var item in document.RootElement.EnumerateArray())
                items.Add(item.Clone());
            return new GitHubPage(false, items, NextPage(response), Etag(response), Remaining(response),
                ResetAt(response), RetryAfter(response));
        }
        catch (HttpRequestException exception)
        {
            throw new GitHubReadException(null, $"{relativePath}: network failure: {Redact(exception.Message)}");
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubReadException(null, $"{relativePath}: request timed out: {Redact(exception.Message)}");
        }
    }

    private async Task<Exception> FailureAsync(string path, HttpResponseMessage response, CancellationToken cancellationToken,
        int? remaining, DateTimeOffset? resetAt, int? retryAfter)
    {
        var detail = Redact((await response.Content.ReadAsStringAsync(cancellationToken)).Trim());
        var message = $"{path}: HTTP {(int)response.StatusCode}: {detail}";
        // decision 53: 403-with-primary-limit-exhausted and 429 are rate limits; every other 403 is a
        // plain error (mirrors the outbox classification precedent).
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            if (remaining == 0)
                return new GitHubRateLimitedException(response.StatusCode, remaining, retryAfter, resetAt, message);
            return new GitHubReadException(response.StatusCode, message);
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return new GitHubRateLimitedException(response.StatusCode, remaining, retryAfter, resetAt, message);
        return new GitHubReadException(response.StatusCode, message);
    }

    /// <summary>Parses `Link: <url>; rel="next"` — first comma-separated segment with rel="next", URL between angle brackets.</summary>
    private static Uri? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
            return null;
        foreach (var segment in string.Join(",", values).Split(','))
        {
            if (!segment.Contains("rel=\"next\"", StringComparison.Ordinal) && !segment.Contains("rel=next", StringComparison.Ordinal))
                continue;
            var start = segment.IndexOf('<');
            var end = segment.IndexOf('>');
            if (start >= 0 && end > start)
                return new Uri(segment[(start + 1)..end]);
        }
        return null;
    }

    private static string? Etag(HttpResponseMessage response)
    {
        var value = response.Headers.ETag?.Tag;
        return string.IsNullOrEmpty(value) ? null : value.Trim('"');
    }

    private static int? Remaining(HttpResponseMessage response)
    {
        var value = Header(response, "X-RateLimit-Remaining");
        return value is { } raw && long.TryParse(raw, out var parsed) && parsed is >= int.MinValue and <= int.MaxValue
            ? (int)parsed
            : null;
    }

    private static DateTimeOffset? ResetAt(HttpResponseMessage response)
    {
        var value = Header(response, "X-RateLimit-Reset");
        return value is { } raw && long.TryParse(raw, out var epoch)
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : null;
    }

    private static int? RetryAfter(HttpResponseMessage response)
    {
        var value = Header(response, "Retry-After");
        return value is { } raw && int.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private string Redact(string message) => message.Replace(_pat, "<redacted>");
}
