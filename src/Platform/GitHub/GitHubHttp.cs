using System.Net;
using System.Net.Http.Headers;

namespace PiAgent.Platform.GitHub;

/// <summary>
/// Shared GitHub REST client mechanics (mirrors the former per-host private copies in the Poller's
/// GitHubReadClient and the Outbox Worker's GitHubWriteClient): header discipline (Bearer PAT,
/// GitHub JSON accept, own user agent), rate-limit header extraction, and PAT redaction from any
/// response-echoed detail. Exception types and failure semantics stay per host — the Poller, the
/// Outbox Worker and the Config Loader classify failures differently by design.
/// </summary>
public static class GitHubHttp
{
    /// <summary>Header discipline: Bearer PAT, GitHub JSON accept, own user agent.</summary>
    public static HttpClient Initialize(HttpClient httpClient, string pat, string userAgent)
    {
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return httpClient;
    }

    /// <summary>Removes the PAT from any text that may echo back from the (hostile) response body.</summary>
    public static string Redact(string text, string pat) =>
        string.IsNullOrEmpty(pat) ? text : text.Replace(pat, "<redacted>");

    public static int? Remaining(HttpResponseMessage response)
    {
        var value = Header(response, "X-RateLimit-Remaining");
        return value is { } raw && long.TryParse(raw, out var parsed) && parsed is >= int.MinValue and <= int.MaxValue
            ? (int)parsed
            : null;
    }

    public static DateTimeOffset? ResetAt(HttpResponseMessage response)
    {
        var value = Header(response, "X-RateLimit-Reset");
        return value is { } raw && long.TryParse(raw, out var epoch)
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : null;
    }

    public static int? RetryAfter(HttpResponseMessage response)
    {
        var value = Header(response, "Retry-After");
        return value is { } raw && int.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
