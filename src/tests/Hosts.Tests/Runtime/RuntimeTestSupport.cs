using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiAgent.Hosts.Tests.Commands;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Hosts.Tests.Orchestration;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Context;
using PiAgent.Orchestrator.Orchestration;
using PiAgent.Platform.Auth;
using PiAgent.RuntimeApi;
using Xunit;

namespace PiAgent.Hosts.Tests.Runtime;

/// <summary>The fake IKomodoClient: records provisioning calls, drives observable container states.</summary>
public sealed class FakeKomodo : IKomodoClient
{
    public List<KomodoDeploymentPlan> Created { get; } = [];
    public Dictionary<string, string> States { get; } = [];
    public List<string> Stopped { get; } = [];
    public bool DeploySucceeds { get; set; } = true;
    /// <summary>False simulates a Komodo outage at stop time (ticket 14: the sweeper retries).</summary>
    public bool StopSucceeds { get; set; } = true;

    public Task<string?> CreateDeploymentAsync(KomodoDeploymentPlan plan, CancellationToken cancellationToken)
    {
        Created.Add(plan);
        States[plan.Name] = "created";
        return Task.FromResult<string?>($"komodo-{plan.Name}");
    }

    public async Task<bool> DeployAndWaitRunningAsync(string deployment, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (!DeploySucceeds)
            return false;
        States[deployment] = "running";
        return true;
    }

    public Task<KomodoContainerSnapshot?> GetContainerStateAsync(string deployment, CancellationToken cancellationToken) =>
        Task.FromResult(States.TryGetValue(deployment, out var state)
            ? new KomodoContainerSnapshot(state, null)
            : null);

    public async Task<bool> StopAsync(string deployment, CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (!StopSucceeds)
            return false;
        Stopped.Add(deployment);
        States[deployment] = "stopped";
        return true;
    }
}

/// <summary>One runtime-api app against the Postgres fixture: temp storage roots, the fake Komodo, and
/// the orchestrator service token. Run/Session/Container/Workspace rows come from production RunPrepare.</summary>
public sealed record RuntimeApi(
    WebApplication App,
    HttpClient ServiceClient,
    AppDbContext Db,
    FakeKomodo Komodo,
    string StorageRoot) : IAsyncDisposable
{
    public const string OrchestratorToken = "tok-orch";

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        await Db.DisposeAsync();
    }
}

public static class RuntimeTestSupport
{
    /// <summary>Seeds repo/workItem + task/job (admission bypassed) and mirrors the claim flow: the
    /// ticket-11 Context Assembler assembles first, the production RunPrepare prepares, and the frozen
    /// snapshot is written via the same freeze helper the dispatcher uses (so /start accepts the run).
    /// `withSnapshot: false` leaves the snapshot missing (negative dispatch-path tests).</summary>
    public static async Task<RunPreparation> SeedPreparedRunAsync(
        RuntimeApi api,
        string versionState = ProfileVersionStates.Ready,
        string? image = "agent-stub:v1",
        bool withSnapshot = true,
        string? bridgeConfiguration = """{"version":"1.0.0","heartbeat_interval":"00:00:15","event_cadence":"00:00:05","behavior":"succeed"}""",
        string? resourceConfiguration = """{"cpu":1.0,"memory":"512MiB"}""")
    {
        var db = api.Db;
        var existing = await db.Repositories.AsNoTracking().FirstOrDefaultAsync();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db, existingRepository: existing);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan, versionState);
        if (image is not null)
            version.ContainerImage = image;
        if (bridgeConfiguration is not null)
            version.BridgeConfiguration = bridgeConfiguration;
        if (resourceConfiguration is not null)
            version.ResourceConfiguration = resourceConfiguration;
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        if (seeded.Command is { } seededCommand)
            seededCommand.SourceBodySnapshot = "Do the stub work.";
        var task = await db.OrchestrationTasks.SingleAsync(x => x.Id == seeded.Task.Id); // tracked: the dispatcher mutates it
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == seeded.Task.WorkflowId);

        // ticket-11 claim flow: assemble BEFORE RunPrepare, then prepare, then freeze (dispatcher order)
        var assembly = await ContextAssembler.AssembleAsync(db, task, version, workflow, workItem, repo, seeded.Command, default);
        if (assembly.TooLarge)
            throw new InvalidOperationException($"seeded context is too large: {assembly.TooLargeDetail}");
        var preparation = await RunPrepare.PrepareAsync(db, task, version, default, assembly.Assembled);
        if (preparation is null)
            throw new InvalidOperationException("RunPrepare refused the seeded claim");
        if (withSnapshot)
        {
            ContextAssemblyFreezable.FreezeOrThrow(db, preparation, assembly.Assembled!);
            await db.SaveChangesAsync();
        }
        return preparation;
    }

    /// <summary>Freezes the (ticket-11-scope) snapshot content exactly like the real dispatcher would.</summary>
    public static void SnapshotWithInput(AppDbContext db, RunPreparation preparation, object input)
    {
        var content = JsonSerializer.Serialize(input);
        db.RunContextSnapshots.Add(new RunContextSnapshot
        {
            Id = Guid.NewGuid(),
            RunId = preparation.Run.Id,
            TaskId = preparation.Task.Id,
            ContentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
            IncludedSources = "[]",
            ExcludedSources = "[]",
            Content = content,
            CreatedAt = DateTime.UtcNow,
        });
    }

    /// <summary>POSTs /start as the orchestrator with the FROZEN context (the same bytes the dispatcher
    /// would carry) and returns (response, raw body).</summary>
    public static Task<HttpResponseMessage> StartAsync(HttpClient client, Guid runId, RunPreparation preparation) =>
        client.PostAsJsonAsync($"/internal/runtime/runs/{runId}/start", new
        {
            agentSessionId = preparation.Session.Id,
            workspaceId = preparation.Workspace.Id,
            agentProfileVersionId = preparation.ProfileVersion.Id,
            input = JsonDocument.Parse(preparation.AssembledContext!.Content).RootElement,
        });

    /// <summary>Reads the per-Run callback token the secret-file delivery would give the Bridge.</summary>
    public static string CallbackToken(RuntimeApi api, Guid runId)
    {
        var workflowId = api.Db.Runs.AsNoTracking()
            .Where(r => r.Id == runId)
            .Join(api.Db.OrchestrationTasks.AsNoTracking(), r => r.TaskId, t => t.Id, (r, t) => t.WorkflowId)
            .Single();
        var runDir = System.IO.Path.Combine(api.StorageRoot, "workflows", workflowId.ToString(), "runs", runId.ToString());
        return File.ReadAllText(System.IO.Path.Combine(runDir, "callback-token"));
    }

    public static async Task<RuntimeApi> NewApiAsync(PostgresFixture fixture)
    {
        await fixture.ResetAsync();
        var storageRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pi-runtime-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storageRoot);
        Directory.CreateDirectory(System.IO.Path.Combine(storageRoot, "secrets"));

        var komodo = new FakeKomodo();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseSetting("ConnectionStrings:DefaultDb", fixture.ConnectionString);
        builder.Logging.ClearProviders();

        builder.Services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(fixture.ConnectionString)
            .UseSnakeCaseNamingConvention());
        builder.Services.Configure<ServiceTokenOptions>(options => options.Orchestrator = RuntimeApi.OrchestratorToken);
        builder.Services.AddSingleton<ServiceTokenFilter>();
        builder.Services.Configure<KomodoOptions>(options =>
        {
            options.Url = "http://komodo.fake";
            options.ServerId = "Local";
            options.AgentNetwork = "pi-agent-net";
            options.CallbackBaseUrl = "http://runtime-api:8080";
        });
        builder.Services.Configure<StorageOptions>(options =>
        {
            options.ManagedRoot = System.IO.Path.Combine(storageRoot, "workflows");
            options.HostRoot = System.IO.Path.Combine(storageRoot, "workflows");
            options.SecretsManagedDir = System.IO.Path.Combine(storageRoot, "secrets");
            options.SecretsHostDir = System.IO.Path.Combine(storageRoot, "secrets");
            options.ContainerUserId = 1000;
        });
        builder.Services.AddSingleton<IKomodoClient>(komodo);
        builder.Services.AddSingleton<RuntimeStorageService>();
        builder.Services.AddSingleton<RunCallbackTokenFilter>();

        var app = builder.Build();
        app.MapRunPipelineApi();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(ServiceAuthExtensions.HeaderName, RuntimeApi.OrchestratorToken);
        var db = app.Services.GetRequiredService<AppDbContext>();
        return new RuntimeApi(app, client, db, komodo, storageRoot);
    }
}
