using PiAgent.OutboxWorker.GitHub;
using PiAgent.BuildWorker.Docker;
using PiAgent.BuildWorker.Builds;
using PiAgent.BuildWorker.Api;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiAgent.BuildWorker;
using PiAgent.Platform.Auth;
using PiAgent.Platform.Workers;
using Xunit;

namespace PiAgent.Hosts.Tests.BuildWorker;

/// <summary>Fake Docker Engine: records build calls, drives inspect results and build outcomes.</summary>
public sealed class FakeDockerEngine : IDockerEngine
{
    public sealed record BuildCall(string Tag, byte[] Tar, string LogFile);
    public enum Behavior { Succeed, Fail, ExecuteError, HangUntilCancellation }

    public List<BuildCall> Builds { get; } = [];
    public Dictionary<string, string> Images { get; } = []; // tag → image id
    public Behavior FailNext { get; set; } = Behavior.Succeed;
    public string FailMessage { get; set; } = "docker build failed: RUN apt-get exited 1";
    /// <summary>ExecuteError count that persists across attempts (for retry-exhaustion tests).</summary>
    public int ExecuteErrorsRemaining { get; set; }

    public Task<EngineImageInfo?> InspectImageAsync(string tag, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Images.TryGetValue(tag, out var id)
            ? new EngineImageInfo(id)
            : null);
    }

    public async Task BuildAsync(string tag, byte[] contextTar, string logFile, CancellationToken cancellationToken)
    {
        Builds.Add(new BuildCall(tag, contextTar, logFile));
        cancellationToken.ThrowIfCancellationRequested();
        if (ExecuteErrorsRemaining > 0)
        {
            ExecuteErrorsRemaining--;
            throw new BuildExecutionException("unix socket transport lost");
        }
        var behavior = FailNext;
        FailNext = Behavior.Succeed; // behaviors are one-shot: the next build runs clean
        switch (behavior)
        {
            case Behavior.Fail:
                await File.AppendAllTextAsync(logFile, "{\"error\":\"" + FailMessage + "\"}\n", cancellationToken);
                throw new DockerBuildException(FailMessage);
            case Behavior.ExecuteError:
                throw new BuildExecutionException("unix socket transport lost");
            case Behavior.HangUntilCancellation:
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            default:
                await File.AppendAllTextAsync(logFile, $"== built {tag} ==\n", cancellationToken);
                Images[tag] = $"sha256:{tag.GetHashCode():x16}";
                return;
        }
    }
}

/// <summary>One build-worker app against a temp state root + fake docker (task 10).</summary>
public sealed record BuildWorkerHost(WebApplication App, HttpClient Client, string StateRoot, FakeDockerEngine Docker, bool CallerOwnedRoot = false) : IAsyncDisposable
{
    public BuildDurableStore Store => App.Services.GetRequiredService<BuildDurableStore>();
    public BuildChannel Channel => App.Services.GetRequiredService<BuildChannel>();
    public BuildOptions Options => App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BuildOptions>>().Value;

    public const string OrchestratorToken = "tok-orch";

    public void SetToken(string? token)
    {
        Client.DefaultRequestHeaders.Remove(ServiceAuthExtensions.HeaderName);
        if (token is not null)
            Client.DefaultRequestHeaders.Add(ServiceAuthExtensions.HeaderName, token);
    }

    /// <summary>Dispatches a build for a well-formed pinned snapshot (Dockerfile + profile.yaml + one file).</summary>
    public Task<HttpResponseMessage> PostBuildAsync(Guid buildId, string profile, int version, string? snapshot = null, string directory = "config/agents/stub")
    {
        var body = JsonSerializer.Serialize(new
        {
            buildId,
            profile,
            profileVersion = version,
            sourceCommitSha = "abc123",
            configSnapshot = snapshot ?? BuildSnapshots.Valid("Hello from the pinned context", "echo hi"),
            buildContext = new { directory },
        });
        return Client.PostAsync("/internal/builds", new StringContent(body, Encoding.UTF8, "application/json"));
    }

    /// <summary>Waits until the build reaches a terminal state (bounded, low-noise polling).</summary>
    public async Task<(bool Terminal, BuildResponse? Status)> WaitTerminalAsync(Guid buildId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            var response = await Client.GetAsync($"/internal/builds/{buildId}");
            if (response.IsSuccessStatusCode)
            {
                var status = JsonSerializer.Deserialize<BuildResponse>(
                    await response.Content.ReadAsStringAsync(),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                if (status is { State: BuildStates.Succeeded or BuildStates.Failed })
                    return (true, status);
            }
            await Task.Delay(25);
        }
        return (false, null);
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        if (CallerOwnedRoot)
            return; // caller-owned roots are cleaned by the caller (restart tests)
        try
        {
            Directory.Delete(StateRoot, recursive: true);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }
}

/// <summary>Pinned snapshot JSON fixtures in the Config Loader shape (files + file_contents).</summary>
public static class BuildSnapshots
{
    /// <summary>Pinned snapshot JSON fixtures in the Config Loader shape (files + file_contents): a
    /// Dockerfile, the profile.yaml and one extra file whose content the caller chooses.</summary>
    public static string Valid(string dockerfileContent, string promptContent, string profile = "stub", int version = 2)
    {
        var content = new Dictionary<string, string>
        {
            ["Dockerfile"] = dockerfileContent,
            ["profile.yaml"] = "description: x\n",
            ["prompts/planner.md"] = promptContent,
        };
        return BuildJson(content, profile, version);
    }

    public static string BuildJson(Dictionary<string, string> content, string profile = "stub", int version = 2)
    {
        var payload = new
        {
            profile,
            source_directory = $"config/agents/{profile}",
            files = content.Keys.OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => new { path, sha256 = Sha256(content[path]) })
                .ToArray(),
            file_contents = content,
        };
        return JsonSerializer.Serialize(payload, BuildJsonOptions);
    }

    private static readonly JsonSerializerOptions BuildJsonOptions = new() { WriteIndented = false };

    public static string Sha256(string content)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public static class BuildTestSupport
{
    public static async Task<BuildWorkerHost> NewHostAsync(
        string? stateRoot = null,
        FakeDockerEngine? docker = null,
        Action<BuildOptions>? configure = null)
    {
        var callerOwnedRoot = stateRoot is not null;
        var root = stateRoot ?? Path.Combine(Path.GetTempPath(), "pi-build-worker-tests", Guid.NewGuid().ToString("N"));
        var engine = docker ?? new FakeDockerEngine();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.Configure<BuildOptions>(options =>
        {
            options.StateRoot = root;
            options.ExecutionRetryDelaySeconds = 0; // tests do not wait out retry delays
            configure?.Invoke(options);
        });
        builder.Services.Configure<ServiceTokenOptions>(options => options.Orchestrator = BuildWorkerHost.OrchestratorToken);
        builder.Services.AddSingleton(engine);
        builder.Services.AddSingleton<BuildDurableStore>();
        builder.Services.AddSingleton<BuildChannel>();
        builder.Services.AddSingleton<RunnerCounters>();
        builder.Services.AddPiWorkerLoop();
        builder.Services.AddHostedService<BuildJobRunner>(services =>
            new BuildJobRunner(
                services.GetRequiredService<BuildDurableStore>(),
                engine,
                services.GetRequiredService<BuildChannel>(),
                services.GetRequiredService<RunnerCounters>(),
                services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BuildOptions>>(),
                services.GetRequiredService<ILogger<BuildJobRunner>>()));
        builder.Services.AddSingleton<IWorkerCycle>(services => new BuildJobSweep(
            services.GetRequiredService<BuildDurableStore>(),
            services.GetRequiredService<BuildChannel>(),
            services.GetRequiredService<ILogger<BuildJobSweep>>()));

        var app = builder.Build();
        app.MapBuildApi();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(ServiceAuthExtensions.HeaderName, BuildWorkerHost.OrchestratorToken);
        return new BuildWorkerHost(app, client, root, engine, callerOwnedRoot);
    }
}
