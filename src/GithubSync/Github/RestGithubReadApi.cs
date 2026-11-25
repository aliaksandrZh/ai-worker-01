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
        foreach (var item in array)
        {
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
                c.GetProperty("issue_number").GetInt64(),
                c.GetProperty("user").GetProperty("login").GetString() ?? "",
                c.GetProperty("body").GetString() ?? "",
                c.GetProperty("created_at").GetDateTime()))
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
            .Where(r => r.GetProperty("submitted_at").GetDateTime() > sinceUtc)
            .Select(r => new GithubReview(
                r.GetProperty("id").GetInt64(),
                prNumber,
                r.GetProperty("user").GetProperty("login").GetString() ?? "",
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

    private async Task<JsonElement.ArrayEnumerator> GetArrayAsync(string url, CancellationToken ct)
    {
        using var response = await httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var list = json.RootElement.EnumerateArray().ToList();
        return (JsonElement.ArrayEnumerator)(IEnumerable<JsonElement>)list.AsEnumerable();
    }
}
