using PiAgent.RuntimeApi.Provisioning;
using PiAgent.RuntimeApi.Runs;
using PiAgent.RuntimeApi.Api;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Platform.Http;

namespace PiAgent.RuntimeApi.Provisioning;

/// <summary>Options for the Runtime API's direct container provisioning (decision 82: Komodo dropped;
/// the Runtime API owns the Docker socket for its `pi-session-*` containers).</summary>
public sealed class ContainerProvisionerOptions
{
    /// <summary>Execution host recorded on `ContainerInstance.HostId` (decision 35: exactly one configured host in V1).</summary>
    public string HostId { get; set; } = "Local";
    /// <summary>Compose network agent containers join to reach the Runtime API callbacks (01 §13).</summary>
    public string AgentNetwork { get; set; } = "pi-agent-net";
    /// <summary>Runtime API address as reachable from agent containers (bridge callback base, 04 §4).</summary>
    public string CallbackBaseUrl { get; set; } = "http://runtime-api:8080";
}

/// <summary>Docker failure — the Runtime API surfaces it as a 502 (transient: retried by the dispatcher).</summary>
public sealed class DockerProvisioningException(string message) : Exception(message);

/// <summary>The container parameters one Run's agent container needs (04 §3: provisioning from Orchestrator-owned rows).</summary>
public sealed record ContainerPlan(
    string Name,
    string Image,
    string[] Env,
    string[] Binds,
    IReadOnlyDictionary<string, string> Labels,
    decimal? Cpus,
    long? MemoryBytes,
    string Network);

/// <summary>Observed container snapshot (03 §5 observed states are written back from observations);
/// exit facts come from `inspect`'s State block.</summary>
public sealed record ContainerSnapshot(string State, long? ExitCode, bool? OomKilled);

/// <summary>
/// The minimal Docker Engine surface this system needs (01 §2: the Runtime API abstracts the
/// provisioner). One container per Run: create → start → inspect → stop → remove over the unix socket
/// (decision 82, same raw-HTTP posture as the Build Worker's engine client and the Host Monitor).
/// Failure semantics (the ones Komodo's locks used to provide): 404 = the container is gone (stop/remove
/// treat it as already-done, inspect reports `removed`); socket/daemon failures throw — desired state is
/// preserved and the caller retries (decision 22 posture).
/// </summary>
public interface IContainerProvisioner
{
    /// <summary>Creates the container; returns the Docker container id (null when creation failed).
    /// A name collision (409) force-removes the stale container and creates again — the name is
    /// `pi-session-{runId}` and provisioning is idempotent-guarded by the ContainerInstance row, so an
    /// existing container can only be a leftover from a crashed provisioning.</summary>
    Task<string?> CreateContainerAsync(ContainerPlan plan, CancellationToken cancellationToken);
    /// <summary>Starts the container then polls its state until `running`; false on exit/timeout.</summary>
    Task<bool> StartAndWaitRunningAsync(string container, TimeSpan timeout, CancellationToken cancellationToken);
    /// <summary>Observed container state; `removed` when Docker reports no such container; throws on outage.</summary>
    Task<ContainerSnapshot> GetContainerStateAsync(string container, CancellationToken cancellationToken);
    /// <summary>`docker stop`; true on stopped/already-stopped/already-gone, false when it cannot stop now.</summary>
    Task<bool> StopAsync(string container, CancellationToken cancellationToken);
    /// <summary>`docker rm` after the container has stopped; true on removed/already-gone.</summary>
    Task<bool> RemoveAsync(string container, CancellationToken cancellationToken);
}

public sealed class DockerProvisioningClient : IContainerProvisioner
{
    private readonly HttpClient _http;
    private readonly ILogger<DockerProvisioningClient> _logger;

    public DockerProvisioningClient(IOptions<DockerOptions> dockerOptions, ILogger<DockerProvisioningClient> logger)
        : this(SocketHttpClient.Create(dockerOptions), logger) { }

    internal DockerProvisioningClient(HttpClient http, ILogger<DockerProvisioningClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<string?> CreateContainerAsync(ContainerPlan plan, CancellationToken cancellationToken)
    {
        var created = await CreateCoreAsync(plan, cancellationToken);
        if (created is not null)
            return created;
        _logger.LogWarning("docker create '{name}' collided — force-removing the stale container", plan.Name);
        await RemoveCoreAsync(plan.Name, force: true, cancellationToken);
        return await CreateCoreAsync(plan, cancellationToken);
    }

    private async Task<string?> CreateCoreAsync(ContainerPlan plan, CancellationToken cancellationToken)
    {
        var hostConfig = new Dictionary<string, object?>
        {
            ["Binds"] = plan.Binds,
            ["Init"] = true, // the Komodo-era extra_args `--init`
            ["NetworkMode"] = plan.Network,
            ["RestartPolicy"] = new Dictionary<string, object?> { ["Name"] = "no" },
        };
        if (plan.Cpus is { } cpus)
            hostConfig["NanoCpus"] = (long)(cpus * 1_000_000_000m);
        if (plan.MemoryBytes is { } memory)
            hostConfig["Memory"] = memory;
        var payload = new Dictionary<string, object?>
        {
            ["Image"] = plan.Image,
            ["Env"] = plan.Env,
            ["Labels"] = plan.Labels,
            ["HostConfig"] = hostConfig,
            ["NetworkingConfig"] = new Dictionary<string, object?>
            {
                ["EndpointsConfig"] = new Dictionary<string, object?> { [plan.Network] = new Dictionary<string, object?>() },
            },
        };
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(
            $"http://localhost/containers/create?name={Uri.EscapeDataString(plan.Name)}", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return null; // the caller's collision policy
        if (!response.IsSuccessStatusCode)
            throw new DockerProvisioningException($"docker create '{plan.Name}' answered {response.StatusCode:D}: {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
    }

    public async Task<bool> StartAndWaitRunningAsync(string container, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync($"http://localhost/containers/{container}/start", content: null, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new DockerProvisioningException($"docker start '{container}': container not found");
        if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.NotModified))
            throw new DockerProvisioningException($"docker start '{container}' answered {response.StatusCode:D}: {body}");

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await GetContainerStateAsync(container, cancellationToken);
            switch (snapshot.State)
            {
                case "running":
                    return true;
                case "exited" or "dead":
                    _logger.LogWarning("docker container {container} exited/dead while waiting for start", container);
                    return false;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        _logger.LogWarning("docker container {container} did not reach running within {timeout}", container, timeout);
        return false;
    }

    public async Task<ContainerSnapshot> GetContainerStateAsync(string container, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync($"http://localhost/containers/{container}/json", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new ContainerSnapshot("removed", null, null);
        if (!response.IsSuccessStatusCode)
            throw new DockerProvisioningException($"docker inspect '{container}' answered {response.StatusCode:D}: {body}");
        using var document = JsonDocument.Parse(body);
        var state = document.RootElement.GetProperty("State");
        var name = state.TryGetProperty("Status", out var status) && status.ValueKind == JsonValueKind.String
            ? status.GetString()!
            : "unknown";
        long? exitCode = state.TryGetProperty("ExitCode", out var exit) && exit.ValueKind == JsonValueKind.Number
            ? exit.GetInt64()
            : null;
        bool? oom = null;
        if (state.TryGetProperty("OOMKilled", out var oomKilled) && oomKilled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            oom = oomKilled.GetBoolean();
        return new ContainerSnapshot(name, exitCode, oom);
    }

    public async Task<bool> StopAsync(string container, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync($"http://localhost/containers/{container}/stop", content: null, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotModified or HttpStatusCode.NotFound)
            return true; // stopped / already stopped / already gone — idempotent
        if (response.StatusCode == HttpStatusCode.Conflict)
            return false; // paused or restarting — cannot stop now, the caller retries
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new DockerProvisioningException($"docker stop '{container}' answered {response.StatusCode:D}: {body}");
    }

    public Task<bool> RemoveAsync(string container, CancellationToken cancellationToken) =>
        RemoveCoreAsync(container, force: false, cancellationToken);

    private async Task<bool> RemoveCoreAsync(string container, bool force, CancellationToken cancellationToken)
    {
        using var response = await _http.DeleteAsync(
            $"http://localhost/containers/{Uri.EscapeDataString(container)}{(force ? "?force=true" : string.Empty)}", cancellationToken);
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
            return true; // removed / already gone — idempotent
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new DockerProvisioningException($"docker rm '{container}' answered {response.StatusCode:D}: {body}");
    }
}
