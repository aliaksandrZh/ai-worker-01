using PiAgent.OutboxWorker.GitHub;
using System.Net;
using System.Text;
using System.Text.Json;
using PiAgent.Platform.GitHub;
using static PiAgent.Platform.GitHub.GitHubHttp;

namespace PiAgent.OutboxWorker.GitHub;

/// <summary>
/// A GitHub write outcome that is not 2xx (Status = the HTTP code) or a network-level failure
/// (Status = null — "ambiguous": the write may still have landed server-side). Rate-limit and
/// Retry-After headers are captured for delivery classification. Messages carry path, status,
/// and response detail — with the PAT redacted even from a (hostile) response echo.
/// </summary>
public sealed class GitHubDeliveryException(
    HttpStatusCode? status,
    int? rateLimitRemaining,
    int? retryAfterSeconds,
    string detail,
    Exception? innerException = null)
    : Exception(detail, innerException)
{
    public HttpStatusCode? Status { get; } = status;

    public int? RateLimitRemaining { get; } = rateLimitRemaining;

    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>
/// GitHub REST calls the Outbox Dispatcher needs: create the system-notification
/// comment and read existing comment bodies for the marker check. Header discipline mirrors the
/// Config Loader's GitHubClientFactory (Bearer PAT, GitHub JSON accept, own user agent).
/// </summary>
public sealed class GitHubWriteClient(HttpClient httpClient, string pat)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient = GitHubHttp.Initialize(httpClient, pat, "pi-agent-outbox-worker");
    private readonly string _pat = string.IsNullOrEmpty(pat) ? "<empty-pat>" : pat;
    public async Task<long> CreateCommentAsync(string owner, string name, long issueNumber, string body, CancellationToken cancellationToken)
    {
        var path = $"repos/{owner}/{name}/issues/{issueNumber}/comments";
        var json = JsonSerializer.Serialize(new { body }, Json);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw await DeliveryFailureAsync(path, response, cancellationToken);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("id", out var id))
                throw new GitHubDeliveryException(response.StatusCode, Remaining(response), RetryAfter(response),
                    $"{path}: 2xx response has no id field");
            return id.GetInt64();
        }
        catch (HttpRequestException exception)
        {
            throw new GitHubDeliveryException(null, null, null, $"{path}: network failure (ambiguous): {exception.Message}", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubDeliveryException(null, null, null, $"{path}: request timed out (ambiguous): {exception.Message}", exception);
        }
    }

    public async Task<IReadOnlyList<string>> ListLatestCommentBodiesAsync(string owner, string name, long issueNumber, CancellationToken cancellationToken)
    {
        var path = $"repos/{owner}/{name}/issues/{issueNumber}/comments?per_page=100&sort=created&direction=desc";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await DeliveryFailureAsync(path, response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var bodies = new List<string>();
        foreach (var element in document.RootElement.EnumerateArray())
            bodies.Add(element.GetProperty("body").GetString() ?? "");
        return bodies;
    }

    /// <summary>The AI-label transition: remove the other three (404 = already absent, fine),
    /// then add `target`; `target = null` removes all four (cancellation/closure). No PATCH — it
    /// would replace the whole label set and clobber the human's labels.</summary>
    public async Task ApplyAiStateLabelAsync(string owner, string name, long issueNumber, string? target,
        CancellationToken cancellationToken)
    {
        var toRemove = target is null
            ? (IEnumerable<string>)PiAgent.Infrastructure.Persistence.AiStateLabels.All
            : PiAgent.Infrastructure.Persistence.AiStateLabels.All.Where(label => label != target);
        foreach (var label in toRemove)
            await DeleteIssueLabelAsync(owner, name, issueNumber, label, cancellationToken);
        if (target is not null)
            await AddIssueLabelAsync(owner, name, issueNumber, target, cancellationToken);
    }

    private async Task AddIssueLabelAsync(string owner, string name, long issueNumber, string label,
        CancellationToken cancellationToken)
    {
        var path = $"repos/{owner}/{name}/issues/{issueNumber}/labels";
        var json = JsonSerializer.Serialize(new { labels = new[] { label } }, Json);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw await DeliveryFailureAsync(path, response, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new GitHubDeliveryException(null, null, null, $"{path}: network failure (ambiguous): {exception.Message}", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubDeliveryException(null, null, null, $"{path}: request timed out (ambiguous): {exception.Message}", exception);
        }
    }

    /// <summary>Removes one label; GitHub answers 404 when the label is not on the issue —
    /// that is the normal, expected outcome of the replace action and counts as success.</summary>
    private async Task DeleteIssueLabelAsync(string owner, string name, long issueNumber, string label,
        CancellationToken cancellationToken)
    {
        var path = $"repos/{owner}/{name}/issues/{issueNumber}/labels/{Uri.EscapeDataString(label)}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, path);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return;
            if (!response.IsSuccessStatusCode)
                throw await DeliveryFailureAsync(path, response, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new GitHubDeliveryException(null, null, null, $"{path}: network failure (ambiguous): {exception.Message}", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubDeliveryException(null, null, null, $"{path}: request timed out (ambiguous): {exception.Message}", exception);
        }
    }

    private async Task<GitHubDeliveryException> DeliveryFailureAsync(string path, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var detail = GitHubHttp.Redact((await response.Content.ReadAsStringAsync(cancellationToken)).Trim(), _pat);
        return new GitHubDeliveryException(
            response.StatusCode,
            Remaining(response),
            RetryAfter(response),
            $"{path}: HTTP {(int)response.StatusCode}: {detail}");
    }

}
