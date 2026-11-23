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
/// Retry-After headers are captured for decision-67 classification. Messages carry path, status,
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
/// GitHub REST calls the Outbox Dispatcher needs (decision 67): create the system-notification
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
