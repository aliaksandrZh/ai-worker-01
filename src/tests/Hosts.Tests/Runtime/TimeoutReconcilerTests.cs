using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PiAgent.Hosts.Tests.Commands;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Hosts.Tests.Orchestration;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Orchestration;
using PiAgent.RuntimeApi;
using Xunit;

namespace PiAgent.Hosts.Tests.Runtime;

/// <summary>
/// Ticket 13: the execution-limit reconciler — the pure verdict matrix (TimeoutEvaluator), the
/// effective-limit cap (§16: profiles may lower but never bypass system constraints), the sweeper
/// cycle against real PostgreSQL (startup/idle/max_run_duration reconciliations recording the
/// identical terminal facts), and the container-death reconciliation with the owner notification.
/// </summary>
public class TimeoutReconcilerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly RuntimeLimits Limits = new()
    {
        StartupTimeout = TimeSpan.FromSeconds(10),
        IdleTimeout = TimeSpan.FromSeconds(10),
        MaxRunDuration = TimeSpan.FromHours(1),
        HeartbeatInterval = TimeSpan.FromSeconds(5),
    };

    // ---- pure verdict matrix (03 §9, decision 68) ----

    [Theory]
    [InlineData(TimeoutVerdict.None, "running", true, false)]        // fresh progress
    [InlineData(TimeoutVerdict.StartupTimeout, "starting", null, false)]
    [InlineData(TimeoutVerdict.RunDuration, "running", true, false)]
    [InlineData(TimeoutVerdict.IdleCandidate, "running", false, null)]
    [InlineData(TimeoutVerdict.IdleConfirmed, "running", false, false)]
    [InlineData(TimeoutVerdict.IdleBusy, "running", false, true)]
    [InlineData(TimeoutVerdict.HeartbeatLost, "running", false, null)]
    public void Evaluate_MatchesTheDecisionMatrix(TimeoutVerdict expected, string status, bool? progressFresh, bool? activity)
    {
        var now = DateTime.UtcNow;
        var started = now - TimeSpan.FromHours(1);
        var maxDuration = expected == TimeoutVerdict.RunDuration ? TimeSpan.FromMinutes(30) : TimeSpan.FromHours(2);
        var lastProgress = progressFresh == true ? now - TimeSpan.FromSeconds(1) : (DateTime?)null;
        DateTime? lastHeartbeat = now - TimeSpan.FromSeconds(1); // the bridge is alive in all these cases
        if (expected == TimeoutVerdict.HeartbeatLost)
            lastHeartbeat = null;

        var verdict = TimeoutEvaluator.Evaluate(status, started, lastProgress, lastHeartbeat, now,
            startupTimeout: TimeSpan.FromMinutes(10), idleTimeout: TimeSpan.FromMinutes(1),
            maxRunDuration: maxDuration, hostMonitorActivity: activity);

        Assert.Equal(expected, verdict);
    }

    [Fact]
    public void Evaluate_StartupNeverBecameReady_TimesOutEvenWithFreshHeartbeat()
    {
        var now = DateTime.UtcNow;
        var verdict = TimeoutEvaluator.Evaluate(RunStatuses.Starting, now - TimeSpan.FromMinutes(11),
            lastProgressAt: null, lastHeartbeatAt: now - TimeSpan.FromSeconds(1), now,
            startupTimeout: TimeSpan.FromMinutes(10), idleTimeout: TimeSpan.FromMinutes(1),
            maxRunDuration: TimeSpan.FromMinutes(30), hostMonitorActivity: true);
        Assert.Equal(TimeoutVerdict.StartupTimeout, verdict);
    }

    [Fact]
    public void Evaluate_DispatchNotYetHandedOff_IsNone()
    {
        var verdict = TimeoutEvaluator.Evaluate(RunStatuses.Pending, startedAt: null, lastProgressAt: null,
            lastHeartbeatAt: null, DateTime.UtcNow,
            startupTimeout: TimeSpan.FromSeconds(1), idleTimeout: TimeSpan.FromSeconds(1),
            maxRunDuration: TimeSpan.FromSeconds(1), hostMonitorActivity: null);
        Assert.Equal(TimeoutVerdict.None, verdict);
    }

    [Fact]
    public void EffectiveLimit_ProfileMayLowerButNeverBypassesTheSystemValue()
    {
        var system = TimeSpan.FromMinutes(10);
        Assert.Equal(TimeSpan.FromMinutes(5), RunReconcileService.EffectiveLimit(TimeSpan.FromMinutes(5), system)); // lowered
        Assert.Equal(system, RunReconcileService.EffectiveLimit(TimeSpan.FromMinutes(20), system)); // capped
        Assert.Equal(system, RunReconcileService.EffectiveLimit(null, system)); // default
    }

    [Fact]
    public void ParseObservation_ReadsPerRunActivityAndAge()
    {
        var now = DateTime.UtcNow;
        var observation = new MonitorObservation
        {
            ObservedAt = now - TimeSpan.FromSeconds(5),
            Containers = """[{"runId":"b5a9f3ba-0000-0000-0000-000000000001","busy":true},{"runId":"b5a9f3ba-0000-0000-0000-000000000002","busy":false}]""",
        };
        var (age, activity) = TimeoutSweeper.ParseObservation(observation, now);
        Assert.NotNull(age);
        Assert.True(activity[Guid.Parse("b5a9f3ba-0000-0000-0000-000000000001")]);
        Assert.False(activity[Guid.Parse("b5a9f3ba-0000-0000-0000-000000000002")]);

        // a corrupted observation never authorizes a kill
        var (corruptAge, corruptActivity) = TimeoutSweeper.ParseObservation(
            new MonitorObservation { ObservedAt = now, Containers = "{not json" }, now);
        Assert.Empty(corruptActivity);
    }

    // ---- sweeper cycle against real PostgreSQL ----

    private sealed class Scope : IAsyncDisposable
    {
        public required FakeKomodo Komodo;
        public required string StorageRoot;
        public TimeoutSweeper Sweeper = null!;
        public RunReconcilerCycle DeathReconciler = null!;

        public async ValueTask DisposeAsync()
        {
            if (Directory.Exists(StorageRoot))
                Directory.Delete(StorageRoot, recursive: true);
            await Task.CompletedTask;
        }
    }

    private Scope NewScope()
    {
        var storageRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pi-timeout-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(storageRoot, "workflows"));
        var komodo = new FakeKomodo();
        var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options
                .UseNpgsql(postgres.ConnectionString)
                .UseSnakeCaseNamingConvention())
            .AddSingleton<IKomodoClient>(komodo)
            .Configure<StorageOptions>(options =>
            {
                options.ManagedRoot = System.IO.Path.Combine(storageRoot, "workflows");
                options.HostRoot = System.IO.Path.Combine(storageRoot, "workflows");
                options.SecretsManagedDir = System.IO.Path.Combine(storageRoot, "secrets");
                options.SecretsHostDir = System.IO.Path.Combine(storageRoot, "secrets");
            })
            .AddSingleton<RuntimeStorageService>()
            
            .BuildServiceProvider();
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        return new Scope
        {
            Komodo = komodo,
            StorageRoot = storageRoot,
            Sweeper = new TimeoutSweeper(scopeFactory, Limits, NullLogger<TimeoutSweeper>.Instance),
            DeathReconciler = new RunReconcilerCycle(scopeFactory, NullLogger<RunReconcilerCycle>.Instance),
        };
    }

    /// <summary>Seeds a run at a given status with the given timeline facts (started N minutes ago).</summary>
    private async Task<Run> SeedRunAsync(string status, TimeSpan startedAgo, TimeSpan? progressAgo = null, TimeSpan? heartbeatAgo = null)
    {
        var db = postgres.NewDbContext();
        var existing = await db.Repositories.AsNoTracking().FirstOrDefaultAsync();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db, existingRepository: existing);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        var preparation = await RunPrepare.PrepareAsync(db, seeded.Task, version, default);
        var now = DateTime.UtcNow;
        seeded.Task.Status = OrchestrationTaskStatuses.Starting;
        seeded.Job.Status = OrchestrationJobStatuses.Completed;
        preparation!.Run.Status = status;
        preparation.Run.StartedAt = now - startedAgo;
        var container = await db.ContainerInstances.SingleAsync(c => c.RunId == preparation.Run.Id);
        container.DeploymentName = $"pi-session-{preparation.Run.Id}"; // the Runtime API writes this at provisioning
        if (progressAgo is { } progress)
            preparation.Run.LastProgressAt = now - progress;
        if (heartbeatAgo is { } heartbeat)
            preparation.Run.LastHeartbeatAt = now - heartbeat;
        await db.SaveChangesAsync();
        return preparation.Run;
    }

    private async Task<Run> ReloadRunAsync(Guid runId)
    {
        var db = postgres.NewDbContext();
        return await db.Runs.AsNoTracking().SingleAsync(r => r.Id == runId);
    }

    [Fact]
    public async Task StartupTimeout_NeverBecameReady_RunFailsWithTerminalFacts()
    {
        await postgres.ResetAsync();
        await using var scope = NewScope();
        var run = await SeedRunAsync(RunStatuses.Starting, startedAgo: TimeSpan.FromMinutes(15), heartbeatAgo: TimeSpan.FromSeconds(1));

        await scope.Sweeper.RunCycleAsync(default);

        var reloaded = await ReloadRunAsync(run.Id);
        Assert.Equal(RunStatuses.Failed, reloaded.Status);
        Assert.Equal(RunErrorCodes.StartupTimeout, reloaded.ErrorCode);
        Assert.StartsWith("reconciled:", reloaded.ErrorMessage);
        Assert.NotNull(reloaded.CompletedAt);
        Assert.NotNull(reloaded.LogStorageReference);
        // the container was stopped and the terminal facts recorded
        Assert.Contains(scope.Komodo.Stopped, d => d == $"pi-session-{run.Id}");
        var db = postgres.NewDbContext();
        var session = await db.AgentSessions.AsNoTracking().SingleAsync(s => s.RunId == run.Id);
        Assert.Equal(AgentSessionStatuses.Terminated, session.Status);
        var workspace = await db.Workspaces.AsNoTracking().SingleAsync(w => w.TaskId == run.TaskId);
        Assert.Equal(WorkspaceStatuses.Ready, workspace.Status);
    }

    [Fact]
    public async Task MaxRunDuration_RunEndsTimedOut()
    {
        await postgres.ResetAsync();
        await using var scope = NewScope();
        var run = await SeedRunAsync(RunStatuses.Running, startedAgo: TimeSpan.FromHours(2), progressAgo: TimeSpan.FromSeconds(1));

        await scope.Sweeper.RunCycleAsync(default);

        var reloaded = await ReloadRunAsync(run.Id);
        Assert.Equal(RunStatuses.TimedOut, reloaded.Status);
        Assert.StartsWith("reconciled:", reloaded.ErrorMessage);
    }

    [Fact]
    public async Task IdleTimeout_ConfirmedHang_FailsIdleTimeout_BusyRunIsSpared()
    {
        await postgres.ResetAsync();
        await using var scope = NewScope();
        var run = await SeedRunAsync(RunStatuses.Running, startedAgo: TimeSpan.FromMinutes(15), progressAgo: TimeSpan.FromMinutes(15), heartbeatAgo: TimeSpan.FromSeconds(1));

        // a fresh observation reporting NO activity for this run → confirmed hang
        var db = postgres.NewDbContext();
        db.MonitorObservations.Add(new MonitorObservation
        {
            Id = Guid.NewGuid(),
            ObservedAt = DateTime.UtcNow,
            ReceivedAt = DateTime.UtcNow,
            Containers = $$"""[{"runId":"{{run.Id}}","busy":false}]""",
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await scope.Sweeper.RunCycleAsync(default);

        var reloaded = await ReloadRunAsync(run.Id);
        Assert.Equal(RunStatuses.Failed, reloaded.Status);
        Assert.Equal(RunErrorCodes.IdleTimeout, reloaded.ErrorCode);

        // now the busy case: a run with an active child process tree is NOT killed for silence
        var busyRun = await SeedRunAsync(RunStatuses.Running, startedAgo: TimeSpan.FromMinutes(15), progressAgo: TimeSpan.FromMinutes(15), heartbeatAgo: TimeSpan.FromSeconds(1));
        db = postgres.NewDbContext();
        db.MonitorObservations.Add(new MonitorObservation
        {
            Id = Guid.NewGuid(),
            ObservedAt = DateTime.UtcNow,
            ReceivedAt = DateTime.UtcNow,
            Containers = $$"""[{"runId":"{{busyRun.Id}}","busy":true}]""",
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        await scope.Sweeper.RunCycleAsync(default);
        var busyReloaded = await ReloadRunAsync(busyRun.Id);
        Assert.Equal(RunStatuses.Running, busyReloaded.Status); // a long-running build/test is not killed

        // and the no-observation case never kills unevidenced either
        var unevidenced = await SeedRunAsync(RunStatuses.Running, startedAgo: TimeSpan.FromMinutes(15), progressAgo: TimeSpan.FromMinutes(15), heartbeatAgo: TimeSpan.FromSeconds(1));
        db = postgres.NewDbContext();
        await db.MonitorObservations.ExecuteDeleteAsync();
        await scope.Sweeper.RunCycleAsync(default);
        Assert.Equal(RunStatuses.Running, (await ReloadRunAsync(unevidenced.Id)).Status);
    }

    [Fact]
    public async Task ContainerDiesWithoutTerminalCallback_RunFailsRuntimeFailureAndNotificationFires()
    {
        await postgres.ResetAsync();
        await using var scope = NewScope();
        var run = await SeedRunAsync(RunStatuses.Running, startedAgo: TimeSpan.FromMinutes(5), progressAgo: TimeSpan.FromSeconds(1), heartbeatAgo: TimeSpan.FromSeconds(1));
        // the container died (observed by Komodo) and never emitted a terminal callback
        scope.Komodo.States[$"pi-session-{run.Id}"] = "exited";

        await scope.DeathReconciler.RunCycleAsync(default);

        var reloaded = await ReloadRunAsync(run.Id);
        Assert.Equal(RunStatuses.Failed, reloaded.Status);
        Assert.Equal(RunErrorCodes.RuntimeFailure, reloaded.ErrorCode);
        Assert.Contains("reconciled: the container died without emitting a terminal callback", reloaded.ErrorMessage);

        // the notification path fires (never silent recovery): the terminal consumer enqueues it
        var db = postgres.NewDbContext();
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == run.TaskId);
        var consumer = new PiAgent.Orchestrator.Orchestration.RunTerminalConsumer(
            new ServiceCollection().AddDbContext<AppDbContext>(options => options
                .UseNpgsql(postgres.ConnectionString).UseSnakeCaseNamingConvention()).BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>());
        try
        {
            await consumer.RunCycleAsync(default);
        }
        catch (Exception exception)
        {
            Assert.Fail($"consumer threw: {exception}");
        }
        var allActions = await db.OutboxActions.AsNoTracking().ToListAsync();
        var notification = allActions.SingleOrDefault(a => a.TaskId == task.Id);
        Assert.True(notification is not null, $"outbox actions: {string.Join(",", allActions.Select(a => $"{a.TaskId}|{a.Payload?.Take(120)}"))}");
        Assert.Contains("reconciled: the container died", notification!.Payload);
    }

    [Fact]
    public async Task KomodoOutage_NothingCorrupted_RunRetriedNextTick()
    {
        await postgres.ResetAsync();
        await using var scope = NewScope();
        var run = await SeedRunAsync(RunStatuses.Starting, startedAgo: TimeSpan.FromMinutes(15));
        scope.Komodo.StopSucceeds = false; // the outage

        await scope.Sweeper.RunCycleAsync(default);

        var reloaded = await ReloadRunAsync(run.Id);
        Assert.Equal(RunStatuses.Starting, reloaded.Status); // desired state preserved, nothing written
        Assert.Null(reloaded.CompletedAt);

        scope.Komodo.StopSucceeds = true; // recovery
        await scope.Sweeper.RunCycleAsync(default);
        Assert.Equal(RunStatuses.Failed, (await ReloadRunAsync(run.Id)).Status);
    }
}
