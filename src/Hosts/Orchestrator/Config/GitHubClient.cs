using System.Net.Http.Headers;
using System.Text.Json;

namespace PiAgent.Orchestrator.Config;

/// <summary>Onboarding metadata for one repository (02 §2).</summary>
public sealed record GitHubRepoInfo(long Id, string FullName, string DefaultBranch);

/// <summary>
/// GitHub REST calls the Config Loader needs at startup (02 §21, decision 66): GET /user for the bot
/// identity check and GET /repos/{owner}/{repo} for repository onboarding metadata. Any non-success is
/// a ConfigException naming the call — the PAT never appears in exceptions or logs (§14, decision 76).
/// Note: startup treats GitHub unavailability as a configuration error — the host fails and compose
/// `restart: unless-stopped` provides the retry.
/// </summary>
public sealed class GitHubClient(HttpClient httpClient, string? pat = null)
{
    private readonly HttpClient _httpClient = Initialize(httpClient, pat);
    private static HttpClient Initialize(HttpClient httpClient, string? pat)
    {
        if (pat is { } token)
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return httpClient;
    }

    public async Task<string> GetAuthenticatedLoginAsync(CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync("user", cancellationToken);
        if (!json.RootElement.TryGetProperty("login", out var login) || login.GetString() is not { } value)
            throw new ConfigException("GET /user: response has no login field");
        return value;
    }

    public async Task<GitHubRepoInfo> GetRepositoryAsync(string owner, string name, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync($"repos/{owner}/{name}", cancellationToken);
        var root = json.RootElement;
        if (!root.TryGetProperty("id", out var id)
            || !root.TryGetProperty("default_branch", out var defaultBranch) || defaultBranch.GetString() is not { } branch)
            throw new ConfigException($"GET /repos/{owner}/{name}: response is missing id or default_branch");
        var repositoryId = id.GetInt64();
        var fullName = root.TryGetProperty("full_name", out var fn) ? fn.GetString() ?? $"{owner}/{name}" : $"{owner}/{name}";
        return new GitHubRepoInfo(repositoryId, fullName, branch);
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var notFound = response.StatusCode == System.Net.HttpStatusCode.NotFound ? " (not found)" : "";
            // decision 65: 401 = GitHub rejected the PAT — revoked/expired/invalid; no fallback PAT exists.
            var rejectedPat = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? " — GitHub rejected the PAT (revoked, expired, or invalid); replace the host-managed PAT file (no automatic fallback, decision 65)"
                : "";
            throw new ConfigException($"GET /{path} failed: HTTP {(int)response.StatusCode}{notFound}{rejectedPat}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}

public static class GitHubClientFactory
{
    /// <summary>Builds the client a Config Loader run uses: Bearer PAT header, GitHub JSON accept header.</summary>
    public static GitHubClient Create(string pat)
    {
        var httpClient = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("pi-agent-config-loader");
        return new GitHubClient(httpClient, pat);
    }
}
