using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PiAgent.Platform.Http;

namespace PiAgent.Orchestrator.Commands;

/// <summary>Authorization verdict shapes for command processing.</summary>
public enum PermissionKind
{
    Allowed,
    /// <summary>Collaborator but below the repository's configured minimum (default write).</summary>
    InsufficientPermission,
    /// <summary>GitHub 404 — not a collaborator (or unknown); a per-user rejection.</summary>
    UserNotFound,
    /// <summary>5xx / network / rate-limited: the command stays `received`, check deferred.</summary>
    Transient,
    /// <summary>401 (revoked/expired PAT) or a regular 403: the PAT lacks scope to perform the check —
    /// a system configuration error surfaced in logs/health, never a per-user rejection, with no
    /// automatic fallback to another PAT.</summary>
    SystemConfigError,
}

public sealed record PermissionOutcome(PermissionKind Kind, TimeSpan? RetryAfter = null);

public enum AllowlistVerdict
{
    Allowed,
    Denied,
}

/// <summary>
/// The authorization gate: write permission via
/// `GET /repos/{owner}/{repo}/collaborators/{username}/permission` — `write`+`admin` accepted,
/// `maintain` treated as `write`, `triage` as `read` — cached per (repository, login) for at most
/// `CacheTtl` (60 s, absolute; error outcomes are never cached, so a removal is honored at worst
/// one TTL late). The repositories.yaml allowlist is a pure evaluation over the synced
/// `Repository.Configuration` jsonb (enabled by default and seeded with the owner;
/// explicitly emptied = filtering off, write baseline remains; malformed configuration fails closed
/// = denied).
/// </summary>
public sealed class CollaboratorPermissionGate(HttpClient httpClient, string pat, TimeSpan? cacheTtlOverride = null, ILogger<CollaboratorPermissionGate>? logger = null)
{
    /// <summary>Results are cached for at most 60 seconds — not a tunable knob.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);

    private readonly TimeSpan _cacheTtl = cacheTtlOverride ?? CacheTtl;
    private readonly ConcurrentDictionary<(string Repository, string Login), (PermissionOutcome Outcome, DateTime ExpiresAtUtc)> _cache = new();

    private readonly HttpClient _httpClient = Initialize(httpClient, pat);

    private static HttpClient Initialize(HttpClient httpClient, string pat)
    {
        // GitHub header discipline mirrors the poller's GitHubReadClient (Bearer PAT, JSON accept, UA)
        httpClient.DefaultRequestHeaders.Authorization = new("Bearer", pat);
        httpClient.DefaultRequestHeaders.Accept.Add(new("application/vnd.github+json"));
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("pi-agent-orchestrator");
        return httpClient;
    }

    public async Task<PermissionOutcome> CheckAsync(
        string owner, string name, string login, string? minimumPermission, CancellationToken cancellationToken)
    {
        var cacheKey = ($"{owner}/{name}".ToLowerInvariant(), login.ToLowerInvariant());
        if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.ExpiresAtUtc)
            return cached.Outcome;

        var outcome = await CheckUncachedAsync(owner, name, login, minimumPermission, cancellationToken);
        if (outcome.Kind is PermissionKind.Allowed or PermissionKind.InsufficientPermission or PermissionKind.UserNotFound)
            _cache[cacheKey] = (outcome, DateTime.UtcNow + _cacheTtl);
        return outcome;
    }

    private async Task<PermissionOutcome> CheckUncachedAsync(
        string owner, string name, string login, string? minimumPermission, CancellationToken cancellationToken)
    {
        string? body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/collaborators/{Uri.EscapeDataString(login)}/permission");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    body = await response.Content.ReadAsStringAsync(cancellationToken);
                    return Classify(body, minimumPermission);

                case HttpStatusCode.NotFound:
                    return new PermissionOutcome(PermissionKind.UserNotFound);

                case HttpStatusCode.Unauthorized:
                    logger?.LogError(
                        "GitHub rejected the permission check with HTTP 401 for {owner}/{repo}/user {login} — the PAT is revoked/expired or lacks scope: system configuration error, no fallback; check deferred",
                        owner, name, login);
                    return new PermissionOutcome(PermissionKind.SystemConfigError);

                case HttpStatusCode.Forbidden when RateLimitRemaining(response) == 0:
                    return RateLimited(response);

                case HttpStatusCode.Forbidden:
                    logger?.LogError(
                        "GitHub returned a non-rate-limit HTTP 403 for the permission check {owner}/{repo}/user {login} — PAT lacks scope: system configuration error; check deferred",
                        owner, name, login);
                    return new PermissionOutcome(PermissionKind.SystemConfigError);

                case (HttpStatusCode)429:
                    logger?.LogWarning("GitHub rate-limited the permission check {owner}/{repo}/user {login} — check deferred", owner, name, login);
                    return RateLimited(response);

                default:
                    logger?.LogWarning("GitHub permission check {owner}/{repo}/user {login} returned HTTP {status} — check deferred",
                        owner, name, login, (int)response.StatusCode);
                    return new PermissionOutcome(PermissionKind.Transient, RetryAfterOf(response));
            }
        }
        catch (HttpRequestException exception)
        {
            logger?.LogWarning(exception, "GitHub collaborators permission lookup failed for {owner}/{repo} user {login} — check deferred", owner, name, login);
            return new PermissionOutcome(PermissionKind.Transient);
        }
        catch (TaskCanceledException exception) when (exception.InnerException is not OperationCanceledException)
        {
            logger?.LogWarning(exception, "GitHub collaborators permission lookup timed out for {owner}/{repo} user {login} — check deferred", owner, name, login);
            return new PermissionOutcome(PermissionKind.Transient);
        }
    }

    private PermissionOutcome Classify(string? body, string? minimumPermission)
    {
        string? role = null;
        string? permission = null;
        try
        {
            using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (json.RootElement.TryGetProperty("role_name", out var roleName) && roleName.ValueKind == JsonValueKind.String)
                role = roleName.GetString();
            if (json.RootElement.TryGetProperty("permission", out var permissionElement) && permissionElement.ValueKind == JsonValueKind.String)
                permission = permissionElement.GetString();
        }
        catch (JsonException)
        {
            // unreadable body is not a per-user verdict — defer and retry
            return new PermissionOutcome(PermissionKind.Transient);
        }

        var observed = RoleRank(role) ?? RoleRank(permission);
        if (observed is null)
        {
            logger?.LogWarning("GitHub permission response carries no known role_name/permission — check deferred");
            return new PermissionOutcome(PermissionKind.Transient);
        }

        var minimum = RoleRank(minimumPermission) ?? 3; // default/unknown → write
        return observed.Value >= minimum
            ? new PermissionOutcome(PermissionKind.Allowed)
            : new PermissionOutcome(PermissionKind.InsufficientPermission);
    }

    /// <summary>Rank: none 0 < read 1 < triage 2 < write 3 < maintain 4 < admin 5.
    /// Unknown values map to null (caller decides the fallback).</summary>
    private static int? RoleRank(string? role) => role?.ToLowerInvariant() switch
    {
        "none" => 0,
        "read" => 1,
        "triage" => 2,
        "write" => 3,
        "maintain" => 4,
        "admin" => 5,
        _ => null,
    };

    private static PermissionOutcome RateLimited(HttpResponseMessage response) =>
        new(PermissionKind.Transient, RetryAfterOf(response));

    private static int RateLimitRemaining(HttpResponseMessage response) =>
        int.TryParse(response.Headers.NonValidated.TryGetValues("X-RateLimit-Remaining", out var values) ? values.FirstOrDefault() : null, out var remaining)
            ? remaining
            : -1;

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        if (response.Headers.NonValidated.TryGetValues("Retry-After", out var retryAfter)
            && int.TryParse(retryAfter.FirstOrDefault(), out var seconds))
            return TimeSpan.FromSeconds(Math.Min(seconds, (int)MaxRetryAfter.TotalSeconds));
        if (response.Headers.NonValidated.TryGetValues("X-RateLimit-Reset", out var reset)
            && long.TryParse(reset.FirstOrDefault(), out var resetEpoch))
        {
            var remaining = DateTimeOffset.FromUnixTimeSeconds(resetEpoch) - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero
                ? remaining > MaxRetryAfter ? MaxRetryAfter : remaining
                : TimeSpan.Zero;
        }
        return null;
    }

    /// <summary>
    /// Allowlist evaluation over the synced `Repository.Configuration` jsonb:
    /// `allowlist.enabled = false` → write baseline (Allowed after the permission check); enabled →
    /// case-insensitive login membership. Malformed JSON denies (fail closed — the configuration
    /// plane syncs a validated shape, so this path means a data bug, not a user mistake).
    /// </summary>
    public static AllowlistVerdict EvaluateAllowlist(string? repositoryConfigurationJson, string login)
    {
        try
        {
            using var json = JsonDocument.Parse(repositoryConfigurationJson ?? "{}");
            var root = json.RootElement;
            if (!root.TryGetProperty("allowlist", out var allowlist) || allowlist.ValueKind != JsonValueKind.Object)
                return AllowlistVerdict.Denied;
            if (allowlist.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is JsonValueKind.False)
                return AllowlistVerdict.Allowed;
            if (!allowlist.TryGetProperty("logins", out var logins) || logins.ValueKind != JsonValueKind.Array)
                return AllowlistVerdict.Denied;
            if (logins.EnumerateArray()
                    .Any(l => l.ValueKind == JsonValueKind.String && string.Equals(l.GetString(), login, StringComparison.OrdinalIgnoreCase)))
                return AllowlistVerdict.Allowed;
            return AllowlistVerdict.Denied;
        }
        catch (JsonException)
        {
            return AllowlistVerdict.Denied;
        }
    }
}
