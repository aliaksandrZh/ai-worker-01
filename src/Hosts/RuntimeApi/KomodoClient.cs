using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PiAgent.RuntimeApi;

/// <summary>Komodo failure — the Runtime API surfaces it as a 502 (transient: retried by the dispatcher).</summary>
public sealed class KomodoException(string message) : Exception(message);

/// <summary>The container parameters one Run's agent container needs (04 §3: provisioning from Orchestrator-owned rows).</summary>
public sealed record KomodoDeploymentPlan(
    string Name,
    string Image,
    string[] ExtraArgs,
    string Volumes,
    string? Environment,
    string? Labels,
    string Network);

/// <summary>Observed container snapshot (03 §5 observed states are written back from observations).</summary>
public sealed record KomodoContainerSnapshot(string State, string? Status);

/// <summary>
/// The minimal Komodo Core RPC surface this system needs (01 §2: the Runtime API abstracts Komodo). Shape-mirrors
/// the reference PoC `api/src/server.mjs` (`komodo_client`): `POST {url}/write|read|execute/{Type}` with the
/// params object as body and `x-api-key`/`x-api-secret` headers (the komodo_client 2.x auth shape).
/// </summary>
public interface IKomodoClient
{
    /// <summary>Creates the deployment; returns the Komodo resource id (null when the reply carries none).</summary>
    Task<string?> CreateDeploymentAsync(KomodoDeploymentPlan plan, CancellationToken cancellationToken);
    /// <summary>`Deploy` (create + start) then polls container state until `running`; false on failure/timeout.</summary>
    Task<bool> DeployAndWaitRunningAsync(string deployment, TimeSpan timeout, CancellationToken cancellationToken);
    /// <summary>Observed container state; null when Komodo reports no such deployment.</summary>
    Task<KomodoContainerSnapshot?> GetContainerStateAsync(string deployment, CancellationToken cancellationToken);
    /// <summary>`StopDeployment` (`docker stop`); false on failure.</summary>
    Task<bool> StopAsync(string deployment, CancellationToken cancellationToken);
}

public sealed class KomodoHttpClient : IKomodoClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly HttpClient _http;
    private readonly KomodoOptions _options;
    private readonly ILogger<KomodoHttpClient> _logger;

    public KomodoHttpClient(HttpClient http, IOptions<KomodoOptions> options, ILogger<KomodoHttpClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        // decision-65: the komodo API key rides the x-api-key/x-api-secret headers (komodo_client auth shape)
        if (!string.IsNullOrEmpty(_options.ApiKey))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", _options.ApiKey);
        if (!string.IsNullOrEmpty(_options.ApiSecret))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-secret", _options.ApiSecret);
    }

    private string Url => _options.Url.TrimEnd('/');

    private async Task<JsonElement> CallAsync(string route, string method, object parameters, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync($"{Url}/{route}/{method}", parameters, JsonOptions, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new KomodoException($"komodo {method} answered {response.StatusCode:D}: {body}");
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error))
            throw new KomodoException($"komodo {method} answered error: {error.ToString()}");
        return document.RootElement.Clone();
    }

    public async Task<string?> CreateDeploymentAsync(KomodoDeploymentPlan plan, CancellationToken cancellationToken)
    {
        var created = await CallAsync("write", "CreateDeployment", new Dictionary<string, object?>
        {
            ["name"] = plan.Name,
            ["config"] = new Dictionary<string, object?>
            {
                ["server_id"] = _options.ServerId,
                ["image"] = new Dictionary<string, object?>
                {
                    ["type"] = "Image",
                    ["params"] = new Dictionary<string, object?> { ["image"] = plan.Image },
                },
                ["extra_args"] = plan.ExtraArgs,
                ["volumes"] = plan.Volumes,
                ["environment"] = plan.Environment,
                ["labels"] = plan.Labels,
                ["network"] = plan.Network,
                ["restart"] = "no",
            },
        }, cancellationToken);
        // Response: Deployment (single object; older shapes array) — the resource id is `id` (or `_id.$oid`).
        var deployment = created.ValueKind == JsonValueKind.Array
            ? created.GetArrayLength() > 0 ? created[0] : default
            : created;
        if (deployment.ValueKind != JsonValueKind.Object)
            return null;
        if (deployment.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            return id.GetString();
        if (deployment.TryGetProperty("_id", out var mongoId))
        {
            if (mongoId.ValueKind == JsonValueKind.String)
                return mongoId.GetString();
            if (mongoId.TryGetProperty("$oid", out var oid) && oid.ValueKind == JsonValueKind.String)
                return oid.GetString();
        }
        return null;
    }

    public async Task<bool> DeployAndWaitRunningAsync(string deployment, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var update = await CallAsync("execute", "Deploy", new Dictionary<string, object?> { ["deployment"] = deployment }, cancellationToken);
        if (update.ValueKind != JsonValueKind.Object || update.TryGetProperty("success", out var success) && success.ValueKind != JsonValueKind.True)
        {
            _logger.LogWarning("komodo Deploy {deployment} failed: {update}", deployment, update.ToString());
            return false;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await GetContainerStateAsync(deployment, cancellationToken);
            if (snapshot is { } state)
            {
                switch (state.State)
                {
                    case "running":
                        return true;
                    case "exited" or "dead":
                        _logger.LogWarning("komodo container {deployment} exited/dead while waiting for start", deployment);
                        return false;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        _logger.LogWarning("komodo container {deployment} did not reach running within {timeout}", deployment, timeout);
        return false;
    }

    public async Task<KomodoContainerSnapshot?> GetContainerStateAsync(string deployment, CancellationToken cancellationToken)
    {
        var response = await CallAsync(
            "read", "GetDeploymentContainer", new Dictionary<string, object?> { ["deployment"] = deployment }, cancellationToken);
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("state", out var state)
            || state.ValueKind != JsonValueKind.String)
            return null;
        var status = response.TryGetProperty("container", out var container) && container.ValueKind == JsonValueKind.Object
            && container.TryGetProperty("status", out var statusValue) && statusValue.ValueKind == JsonValueKind.String
                ? statusValue.GetString()
                : null;
        return new KomodoContainerSnapshot(state.GetString()!, status);
    }

    public async Task<bool> StopAsync(string deployment, CancellationToken cancellationToken)
    {
        var update = await CallAsync("execute", "StopDeployment", new Dictionary<string, object?> { ["deployment"] = deployment }, cancellationToken);
        if (update.ValueKind != JsonValueKind.Object || update.TryGetProperty("success", out var success) && success.ValueKind != JsonValueKind.True)
        {
            _logger.LogWarning("komodo StopDeployment {deployment} failed: {update}", deployment, update.ToString());
            return false;
        }
        return true;
    }
}
