using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using PiAgent.Platform.GitHub;
using static PiAgent.Platform.GitHub.GitHubHttp;
using PiAgent.Platform.Http;

namespace PiAgent.Poller.GitHub;

/// <summary>Result of the deleted-comment sweep's direct re-check: GitHub reports the
/// resource with `Found=false` exactly on a confirmed 404 — every other outcome is an exception.</summary>
public sealed record GitHubSingle(bool Found, JsonElement? Item);

/// <summary>One GitHub REST list page: parsed items, `Link` rel="next" target, and the
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

/// <summary>One Projects v2 GraphQL listing page: raw item nodes + cursor pagination (GraphQL has
/// no conditional requests — every pass lists the board fully; the list-diff dedupe keeps it cheap).</summary>
public sealed record GraphQLItemsPage(IReadOnlyList<JsonElement> Nodes, bool HasNextPage, string? EndCursor);

/// <summary>
/// GitHub REST reads the Poller needs: list endpoints with page size 100, `Link`
/// header paging, `If-None-Match` conditional requests, and `Retry-After`/`X-RateLimit-Reset`
/// extraction. Header discipline mirrors the Outbox worker's GitHubWriteClient: Bearer PAT, GitHub
/// JSON accept, own user agent. Exception messages carry path, status, and response detail — with the
/// PAT redacted even from a (hostile) response echo.
/// </summary>
public sealed class GitHubReadClient(HttpClient httpClient, string pat)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient = GitHubHttp.Initialize(httpClient, pat, "pi-agent-poller");
    private readonly string _pat = string.IsNullOrEmpty(pat) ? "<empty-pat>" : pat;

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
            throw new GitHubReadException(null, $"{relativePath}: network failure: {GitHubHttp.Redact(exception.Message, _pat)}");
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubReadException(null, $"{relativePath}: request timed out: {GitHubHttp.Redact(exception.Message, _pat)}");
        }
    }

    /// <summary>Single-object GET for direct re-checks (deleted-comment sweep): `Found=false`
    /// exactly on a confirmed 404; 200 yields the parsed object. Any other status keeps the shared
    /// classification — rate-limit exceptions propagate to the cycle, other failures throw
    /// GitHubReadException so ambiguous outcomes can never mean deletion.</summary>
    public async Task<GitHubSingle> GetSingleAsync(string relativePath, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(relativePath, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new GitHubSingle(false, null);
            if (!response.IsSuccessStatusCode)
                throw await FailureAsync(relativePath, response, cancellationToken, Remaining(response),
                    ResetAt(response), RetryAfter(response));
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return new GitHubSingle(true, document.RootElement.Clone());
        }
        catch (HttpRequestException exception)
        {
            throw new GitHubReadException(null, $"{relativePath}: network failure: {GitHubHttp.Redact(exception.Message, _pat)}");
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubReadException(null, $"{relativePath}: request timed out: {GitHubHttp.Redact(exception.Message, _pat)}");
        }
    }

    private async Task<Exception> FailureAsync(string path, HttpResponseMessage response, CancellationToken cancellationToken,
        int? remaining, DateTimeOffset? resetAt, int? retryAfter)
    {
        var detail = GitHubHttp.Redact((await response.Content.ReadAsStringAsync(cancellationToken)).Trim(), _pat);
        var message = $"{path}: HTTP {(int)response.StatusCode}: {detail}";
        // 403-with-primary-limit-exhausted and 429 are rate limits; every other 403 is a
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

    /// <summary>
    /// Projects v2 item listing through GraphQL: the REST `/projectsV2/{n}/items` endpoint does not
    /// return field values (only the built-in Title), so column states — the Status single-select
    /// included — are readable exclusively via GraphQL. Cursor-paginated, 100 items per page.
    /// </summary>
    private const string ProjectItemsQuery = """
        query($login: String!, $number: Int!, $cursor: String) {
          user(login: $login) {
            projectV2(number: $number) {
              items(first: 100, after: $cursor) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  databaseId
                  id
                  type
                  createdAt
                  updatedAt
                  fieldValues(first: 30) {
                    nodes {
                      __typename
                      ... on ProjectV2ItemFieldSingleSelectValue { name field { ... on ProjectV2FieldCommon { id name } } }
                      ... on ProjectV2ItemFieldTextValue { text field { ... on ProjectV2FieldCommon { id name } } }
                      ... on ProjectV2ItemFieldDateValue { date field { ... on ProjectV2FieldCommon { id name } } }
                      ... on ProjectV2ItemFieldNumberValue { number field { ... on ProjectV2FieldCommon { id name } } }
                      ... on ProjectV2ItemFieldIterationValue { title field { ... on ProjectV2FieldCommon { id name } } }
                    }
                  }
                  content {
                    __typename
                    ... on Issue { number title repository { nameWithOwner } }
                    ... on PullRequest { number title repository { nameWithOwner } }
                  }
                }
              }
            }
          }
        }
        """;

    public async Task<GraphQLItemsPage> ListProjectItemsAsync(string owner, int number, string? cursor,
        CancellationToken cancellationToken)
    {
        var variables = new Dictionary<string, object?> { ["login"] = owner, ["number"] = number, ["cursor"] = cursor };
        using var request = new HttpRequestMessage(HttpMethod.Post, "graphql")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { query = ProjectItemsQuery, variables }, Json),
                System.Text.Encoding.UTF8, "application/json"),
        };
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw await FailureAsync("graphql", response, cancellationToken, Remaining(response),
                    ResetAt(response), RetryAfter(response));
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
                throw new GitHubReadException(null, "graphql: no data in response: " +
                    GitHubHttp.Redact(root.TryGetProperty("errors", out var errors)
                        ? errors.GetRawText()
                        : root.GetRawText(), _pat));
            var items = data.GetProperty("user").GetProperty("projectV2").GetProperty("items");
            var pageInfo = items.GetProperty("pageInfo");
            var nodes = items.GetProperty("nodes").EnumerateArray().Select(n => n.Clone()).ToList();
            return new GraphQLItemsPage(nodes, pageInfo.GetProperty("hasNextPage").GetBoolean(),
                pageInfo.TryGetProperty("endCursor", out var endCursor) && endCursor.ValueKind == JsonValueKind.String
                    ? endCursor.GetString()
                    : null);
        }
        catch (HttpRequestException exception)
        {
            throw new GitHubReadException(null, $"graphql: network failure: {GitHubHttp.Redact(exception.Message, _pat)}");
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubReadException(null, $"graphql: request timed out: {GitHubHttp.Redact(exception.Message, _pat)}");
        }
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
}
