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

    /// <summary>One named label add/remove (the review loop's fix-required marker). Idempotent by
    /// construction: POST on a present label converges, DELETE on an absent one answers 404 = fine.</summary>
    public async Task SetIssueLabelAsync(string owner, string name, long issueNumber, string label, bool add,
        CancellationToken cancellationToken)
    {
        if (add)
            await AddIssueLabelAsync(owner, name, issueNumber, label, cancellationToken);
        else
            await DeleteIssueLabelAsync(owner, name, issueNumber, label, cancellationToken);
    }

    /// <summary>The AI-driven board status move: resolves the single-select field's id and the
    /// target option's id by exact-case name (a fresh GraphQL query per delivery — the schema may
    /// have changed since the enqueue), then applies updateProjectV2ItemFieldValue. Idempotent by
    /// construction: setting the same value twice converges. A missing field/option is a payload
    /// reality mismatch (the column was renamed or removed) — a permanent failure.
    /// </summary>
    public async Task SetProjectItemStatusAsync(string owner, int projectNumber, string itemNodeId,
        string fieldName, string status, CancellationToken cancellationToken)
    {
        var (projectId, fieldId, optionId) = await ResolveStatusIdsAsync(owner, projectNumber, fieldName, status,
            cancellationToken);
        const string mutation = """
            mutation($project: ID!, $item: ID!, $field: ID!, $option: String!) {
              updateProjectV2ItemFieldValue(input: { projectId: $project, itemId: $item, fieldId: $field, value: { singleSelectOptionId: $option } }) {
                projectV2Item { id }
              }
            }
            """;
        var variables = new Dictionary<string, object?>
        {
            ["project"] = projectId, ["item"] = itemNodeId, ["field"] = fieldId, ["option"] = optionId,
        };
        await GraphQLAsync(mutation, variables, expectDataPath: "updateProjectV2ItemFieldValue", cancellationToken);
    }

    /// <summary>Queries the project's single-select fields and resolves (project node id, field id,
    /// option id) for the exact-case names; throws with a 422-status exception when a name is missing.</summary>
    private async Task<(string ProjectId, string FieldId, string OptionId)> ResolveStatusIdsAsync(
        string owner, int projectNumber, string fieldName, string status, CancellationToken cancellationToken)
    {
        const string query = """
            query($login: String!, $number: Int!) {
              user(login: $login) {
                projectV2(number: $number) {
                  id
                  fields(first: 100) {
                    nodes {
                      ... on ProjectV2SingleSelectField { id name options { id name } }
                    }
                  }
                }
              }
            }
            """;
        var variables = new Dictionary<string, object?> { ["login"] = owner, ["number"] = projectNumber };
        var root = await GraphQLAsync(query, variables, expectDataPath: "user", cancellationToken);
        var project = root.GetProperty("user").GetProperty("projectV2");
        var projectId = project.GetProperty("id").GetString()
            ?? throw Permanent("graphql: projectV2.id is null");
        foreach (var field in project.GetProperty("fields").GetProperty("nodes").EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.Object
                || !field.TryGetProperty("name", out var nameElement)
                || !string.Equals(nameElement.GetString(), fieldName, StringComparison.Ordinal))
                continue;
            var fieldId = field.GetProperty("id").GetString()
                ?? throw Permanent($"graphql: field '{fieldName}' has no id");
            foreach (var option in field.GetProperty("options").EnumerateArray())
            {
                if (!string.Equals(option.GetProperty("name").GetString(), status, StringComparison.Ordinal))
                    continue;
                var optionId = option.GetProperty("id").GetString()
                    ?? throw Permanent($"graphql: option '{status}' has no id");
                return (projectId, fieldId, optionId);
            }
            throw Permanent($"board '{owner}/{projectNumber}': field '{fieldName}' has no option '{status}'");
        }
        throw Permanent($"board '{owner}/{projectNumber}' has no single-select field '{fieldName}'");

        GitHubDeliveryException Permanent(string detail) => new(HttpStatusCode.UnprocessableEntity, null, null, detail);
    }

    /// <summary>One GraphQL POST: 2xx with a `data` payload returns the data root; GraphQL `errors`
    /// (200 or not), non-2xx and transport failures classify into GitHubDeliveryException exactly
    /// like the REST calls (network failures ambiguous).</summary>
    private async Task<JsonElement> GraphQLAsync(string query, Dictionary<string, object?> variables,
        string expectDataPath, CancellationToken cancellationToken)
    {
        const string path = "graphql";
        var json = JsonSerializer.Serialize(new { query, variables }, Json);
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
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null
                || !data.TryGetProperty(expectDataPath, out var expected) || expected.ValueKind == JsonValueKind.Null)
            {
                var errors = root.TryGetProperty("errors", out var errorElement)
                    ? errorElement.GetRawText() : root.GetRawText();
                throw new GitHubDeliveryException(HttpStatusCode.BadGateway, Remaining(response), RetryAfter(response),
                    $"{path}: no data.{expectDataPath} in response: {GitHubHttp.Redact(errors, _pat)}");
            }
            return data.Clone();
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
