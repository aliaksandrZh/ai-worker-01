using System.Text.Json;
using PiAgent.Platform.GitHub;

namespace PiAgent.GithubSync.Github;

/// <summary>The production GitHub read adapter: REST, per_page paging, since-params.</summary>
public sealed class RestGithubReadApi(HttpClient httpClient) : IGithubReadApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<GithubItem>> ListItemsUpdatedSinceAsync(
        string owner, string repo, DateTime sinceUtc, CancellationToken ct)
    {
        var since = sinceUtc == DateTime.MinValue ? "" : $"&since={Uri.EscapeDataString(sinceUtc.ToString("O"))}";
        var url = $"repos/{owner}/{repo}/issues?state=all&per_page=100&sort=updated&direction=asc" + since;
        var array = await GetArrayAsync(url, ct);
        var items = new List<GithubItem>();
        System.Console.Error.WriteLine("[dbg] items parsed start: " + array.Count);
        foreach (var item in array)
        {
            System.Console.Error.WriteLine("[dbg] parsing item " + item.GetProperty("number").GetInt64());
            items.Add(new GithubItem(
                item.GetProperty("number").GetInt64(),
                item.TryGetProperty("pull_request", out _),
                item.GetProperty("title").GetString() ?? "",
                item.GetProperty("body").GetString() ?? "",
                item.GetProperty("state").GetString() ?? "open",
                item.GetProperty("labels").EnumerateArray()
                    .Select(l => l.GetProperty("name").GetString() ?? "")
                    .ToList(),
                item.GetProperty("updated_at").GetDateTime()));
        }

        return items;

    }

    public async Task<IReadOnlyList<GithubComment>> ListCommentsSinceAsync(
        string owner, string repo, DateTime sinceUtc, CancellationToken ct)
    {
        var since = sinceUtc == DateTime.MinValue ? "" : $"&since={Uri.EscapeDataString(sinceUtc.ToString("O"))}";
        var url = $"repos/{owner}/{repo}/issues/comments?per_page=100&sort=created&direction=asc" + since;
        var array = await GetArrayAsync(url, ct);
        return array.Select(c => new GithubComment(
                c.GetProperty("id").GetInt64(),
                c.TryGetProperty("issue_number", out var issueNumber) ? issueNumber.GetInt64() : 0,
                c.TryGetProperty("user", out var user) && user.ValueKind is JsonValueKind.Object
                    ? user.GetProperty("login").GetString() ?? ""
                    : "ghost",
                c.GetProperty("body").GetString() ?? "",
                c.TryGetProperty("created_at", out var createdAt) ? createdAt.GetDateTime() : DateTime.UtcNow))
            .ToList();
    }

    public async Task<string?> GetPullHeadShaAsync(string owner, string repo, long prNumber, CancellationToken ct)
    {
        using var response = await httpClient.GetAsync($"repos/{owner}/{repo}/pulls/{prNumber}", ct);
        if (!response.IsSuccessStatusCode)
            return null;
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("head").GetProperty("sha").GetString();
    }

    public async Task<IReadOnlyList<GithubReview>> ListReviewsSinceAsync(
        string owner, string repo, long prNumber, DateTime sinceUtc, CancellationToken ct)
    {
        var array = await GetArrayAsync($"repos/{owner}/{repo}/pulls/{prNumber}/reviews?per_page=100", ct);
        return array
            .Where(r => r.TryGetProperty("submitted_at", out var submitted) && submitted.ValueKind == JsonValueKind.String
                        && submitted.GetDateTime() > sinceUtc)
            .Select(r => new GithubReview(
                r.GetProperty("id").GetInt64(),
                prNumber,
                r.TryGetProperty("user", out var user) && user.ValueKind is JsonValueKind.Object
                    ? user.GetProperty("login").GetString() ?? ""
                    : "ghost",
                r.GetProperty("state").GetString() ?? "",
                r.GetProperty("body").GetString() ?? "",
                r.GetProperty("submitted_at").GetDateTime()))
            .ToList();
    }

    public async Task<string?> GetPermissionAsync(string owner, string repo, string login, CancellationToken ct)
    {
        using var response = await httpClient.GetAsync(
            $"repos/{owner}/{repo}/collaborators/{login}/permission", ct);
        if (!response.IsSuccessStatusCode)
            return null;
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("permission").GetString();
    }

    private async Task<IReadOnlyList<JsonElement>> GetArrayAsync(string url, CancellationToken ct)
    {
        System.Console.Error.WriteLine($"[dbg] GET {url}");
        using var response = await httpClient.GetAsync(url, ct);
        System.Console.Error.WriteLine($"[dbg] GET done {response.StatusCode}");
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.EnumerateArray().ToList();
    }
}
