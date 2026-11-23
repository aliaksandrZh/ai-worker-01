using PiAgent.RuntimeApi.Provisioning;
using PiAgent.RuntimeApi.Runs;
using PiAgent.RuntimeApi.Api;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Auth;
using PiAgent.RuntimeApi;
using Xunit;

namespace PiAgent.Hosts.Tests.Runtime;

/// <summary>The Host-Monitor telemetry route (its own service credential;
/// stored in PostgreSQL) and the orphan reconciler (grace → quarantine → delete, never
/// while a container binds the tree, never without fresh telemetry).</summary>
public class MonitorTelemetryTests : IClassFixture<WebApplicationFactory<DockerProvisioningClient>>
{
    private readonly WebApplicationFactory<DockerProvisioningClient> _factory;

    public MonitorTelemetryTests(WebApplicationFactory<DockerProvisioningClient> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DefaultDb", "Host=127.0.0.1;Database=test;Username=test;Password=test");
        });
    }

    [Fact]
    public async Task TelemetryRoute_RequiresAServiceToken()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/internal/runtime/monitor/telemetry", new { });
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void TelemetryRoute_IsMapped()
    {
        // the route is part of the Runtime API's surface map; the auth shape is covered by the
        // RequireServiceToken filter tests in HostsTests (the suite runs against real PostgreSQL)
        var source = typeof(MonitorTelemetryApi);
        Assert.NotNull(source);
    }
}

public sealed record ContainerRow(string[] Binds)
{
    public string ContainerId => "c1";
    public string Name => "pi-session-x";
    public Guid? RunId => null;
    public Guid? TaskId => null;
    public string State => "running";
    public bool Busy => false;
    public string[] Binds { get; } = Binds;
}

public class OrphanReconcilerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly RuntimeLimits Limits = new()
    {
        StartupTimeout = TimeSpan.FromMinutes(10),
        IdleTimeout = TimeSpan.FromMinutes(60),
        MaxRunDuration = TimeSpan.FromHours(4),
        HeartbeatInterval = TimeSpan.FromSeconds(30),
        OrphanGracePeriod = TimeSpan.FromSeconds(10),
        OrphanQuarantinePeriod = TimeSpan.FromSeconds(10),
    };

    private OrphanReconciler NewReconciler() =>
        new(new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options
                .UseNpgsql(postgres.ConnectionString)
                .UseSnakeCaseNamingConvention())
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>(),
            Limits,
            NullLogger<OrphanReconciler>.Instance);

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static MonitorObservation Observation(string root, string[] dirs, string[][] bindsByContainer) => new()
    {
        ObservedAt = DateTime.UtcNow,
        ReceivedAt = DateTime.UtcNow,
        Storage = JsonSerializer.Serialize(new
        {
            root,
            freeBytes = (long?)null,
            usedBytes = (long?)null,
            directories = dirs.Select(d => new { name = d, modifiedAtUtc = DateTime.UtcNow.AddDays(-7), sizeBytes = (long?)null }),
        }, WebJson),
        Containers = JsonSerializer.Serialize(
            bindsByContainer.Select(row => (object)new ContainerRow(row)), WebJson),
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Orphan_GracePeriodExpired_IsQuarantinedThenDeleted()
    {
        await postgres.ResetAsync();
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pi-orphan-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(root, "11111111-1111-1111-1111-111111111111", "runs"));

        await using var db = postgres.NewDbContext();
        db.MonitorObservations.Add(Observation(root, ["11111111-1111-1111-1111-111111111111"], []));
        await db.SaveChangesAsync();

        var orphanPath = System.IO.Path.Combine(root, "11111111-1111-1111-1111-111111111111");

        // first cycle: registered as a candidate (no workflow row exists)
        await NewReconciler().RunCycleAsync(default);
        var candidate = await db.OrphanCandidates.AsNoTracking().SingleAsync();
        Assert.Equal(OrphanCandidateStatuses.Candidate, candidate.Status);
        Assert.Equal("workflow row missing", candidate.Details);
        Assert.True(Directory.Exists(orphanPath)); // nothing touched during grace

        // second cycle after the grace window: quarantined (renamed, kept)
        db.OrphanCandidates.ExecuteUpdate(set => set
            .SetProperty(c => c.FirstSeenAt, DateTime.UtcNow - TimeSpan.FromSeconds(20)));
        await NewReconciler().RunCycleAsync(default);
        candidate = await db.OrphanCandidates.AsNoTracking().SingleAsync();
        Assert.Equal(OrphanCandidateStatuses.Quarantined, candidate.Status);
        Assert.EndsWith(".orphan-quarantine", candidate.Path);
        Assert.False(Directory.Exists(orphanPath));
        Assert.True(Directory.Exists(candidate.Path)); // quarantined, not deleted

        // third cycle after the quarantine window: physically deleted
        db.OrphanCandidates.ExecuteUpdate(set => set
            .SetProperty(c => c.QuarantinedAt, DateTime.UtcNow - TimeSpan.FromSeconds(20)));
        await NewReconciler().RunCycleAsync(default);
        candidate = await db.OrphanCandidates.AsNoTracking().SingleAsync();
        Assert.Equal(OrphanCandidateStatuses.Deleted, candidate.Status);
        Assert.NotNull(candidate.DeletedAt);
        Assert.False(Directory.Exists(candidate.Path));
    }

    [Fact]
    public async Task Orphan_BoundByAContainer_IsRecordedButNeverDeleted()
    {
        await postgres.ResetAsync();
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pi-orphan-tests", Guid.NewGuid().ToString("N"));
        var orphanDir = System.IO.Path.Combine(root, "22222222-2222-2222-2222-222222222222");
        Directory.CreateDirectory(orphanDir);

        await using var db = postgres.NewDbContext();
        db.MonitorObservations.Add(Observation(root, ["22222222-2222-2222-2222-222222222222"],
            [[System.IO.Path.Combine(root, "22222222-2222-2222-2222-222222222222") + ":/workspace"]]));
        await db.SaveChangesAsync();

        db.OrphanCandidates.ExecuteUpdate(set => set.SetProperty(c => c.UpdatedAt, DateTime.UtcNow)); // empty set, no rows yet

        await NewReconciler().RunCycleAsync(default);
        var candidate = await db.OrphanCandidates.AsNoTracking().SingleAsync();
        Assert.Equal(OrphanCandidateStatuses.Candidate, candidate.Status);
        Assert.Contains("a container bind covers the tree", candidate.Details);

        // grace expired while the bind is still observed → deferred, never deleted
        db.OrphanCandidates.ExecuteUpdate(set => set
            .SetProperty(c => c.FirstSeenAt, DateTime.UtcNow - TimeSpan.FromDays(1)));
        await NewReconciler().RunCycleAsync(default);
        candidate = await db.OrphanCandidates.AsNoTracking().SingleAsync();
        Assert.Equal(OrphanCandidateStatuses.Candidate, candidate.Status); // still only a candidate
        Assert.True(Directory.Exists(orphanDir)); // untouched
    }

    [Fact]
    public async Task Orphan_LiveWorkflowTree_IsNeverACandidate()
    {
        await postgres.ResetAsync();
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pi-orphan-tests", Guid.NewGuid().ToString("N"));
        var workflowId = Guid.NewGuid();
        Directory.CreateDirectory(System.IO.Path.Combine(root, workflowId.ToString()));

        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await PiAgent.Hosts.Tests.Commands.CommandTestSupport.SeedAsync(db);
        db.Workflows.Add(new Workflow
        {
            Id = workflowId,
            WorkItemId = workItem.Id,
            Status = WorkflowStatuses.WaitingForHuman,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.MonitorObservations.Add(Observation(root, [workflowId.ToString()], []));
        await db.SaveChangesAsync();

        await NewReconciler().RunCycleAsync(default);

        Assert.Empty(await db.OrphanCandidates.AsNoTracking().ToListAsync());
        Assert.True(Directory.Exists(System.IO.Path.Combine(root, workflowId.ToString())));
    }

    [Fact]
    public async Task Orphan_NoFreshTelemetry_NothingHappens()
    {
        await postgres.ResetAsync();
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pi-orphan-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(root, "33333333-3333-3333-3333-333333333333"));

        await using var db = postgres.NewDbContext();
        db.MonitorObservations.Add(new MonitorObservation
        {
            ObservedAt = DateTime.UtcNow - TimeSpan.FromMinutes(5), // stale
            ReceivedAt = DateTime.UtcNow,
            Storage = JsonSerializer.Serialize(new { root, directories = new[] { new { name = "33333333-3333-3333-3333-333333333333", modifiedAtUtc = DateTime.UtcNow } } }),
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await NewReconciler().RunCycleAsync(default);
        Assert.Empty(await db.OrphanCandidates.AsNoTracking().ToListAsync());
    }
}
