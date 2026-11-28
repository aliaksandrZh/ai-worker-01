using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiAgent.Events;
using PiAgent.Events.Postgres;
using PiAgent.RunWorker;
using PiAgent.RunWorker.Docker;
using PiAgent.Tests.Postgres;
using Xunit;

namespace PiAgent.RunWorker.Tests;

/// <summary>
/// The watchdog's exit race (run 52's false RunFailed): grace runs from the container's
/// actual exit — not the run's start — and an already-recorded terminal callback is never
/// raced into a failure.
/// </summary>
[Collection("PostgresLog")]
public sealed class RunWatchdogTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly EventQueueStore _log;
    private readonly RecordingProvisioner _docker = new();
    private readonly RunRegistryStore _store;
    private readonly RunWorkerConsumer _consumer;
    private readonly RunWatchdog _watchdog;

    public Task InitializeAsync()
    {
        return ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task ResetAsync()
    {
        await _fx.ResetCoreAsync();
        await RunContainerSchema.EnsureAsync(_fx);
        await RunContainerSchema.ResetAsync(_fx);
    }

    public RunWatchdogTests(PostgresFixture fx)
    {
        _fx = fx;
        _log = fx.Log;
        _store = RunContainerSchema.Store(fx);
        _consumer = new RunWorkerConsumer(
            _docker, _log, _log, _store,
            Options.Create(new RunWorkerOptions
            {
                WorkspaceRoot = Path.Combine(Path.GetTempPath(), "pi-watchdog-tests", Guid.NewGuid().ToString("N")),
            }),
            NullLogger<RunWorkerConsumer>.Instance);
        _watchdog = new RunWatchdog(
            _store, _docker, _consumer,
            Options.Create(new RunWorkerOptions()),
            NullLogger<RunWatchdog>.Instance);
    }

    private async Task StartRunAsync(long runId = 7)
    {
        var input = new RunInput
        {
            Owner = "o",
            Repo = "r",
            ItemNumber = 1,
            PrNumber = null,
            Command = "plan",
            PlanCommentId = 0,
            Profile = "planner",
            AgentBranch = null,
            DefaultBranch = "main",
            ReviewHeadSha = null,
        };
        // the registry reads run facts back from orch_run — seed it with the same input
        await RunContainerSchema.SeedRunAsync(_fx, runId, taskId: 3,
            input: System.Text.Json.JsonSerializer.Serialize(input,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        await _consumer.HandleAsync(new RunScheduled { RunId = runId, TaskId = 3, WorkflowId = 2, Input = input, Secrets = null },
            attempt: 1, causationId: 0, CancellationToken.None);
    }

    [Fact]
    public async Task A_recently_exited_container_is_not_failed_within_the_grace()
    {
        await StartRunAsync();
        _docker.NextSnapshot = new ContainerSnapshot("exited", 0, false, DateTime.UtcNow.AddSeconds(-10));

        await _watchdog.SweepAsync(CancellationToken.None);

        Assert.DoesNotContain(await _fx.AppendedAsync(), e => e is RunFailed);
        Assert.Contains(await _store.LoadUnfinishedAsync(), e => e.RunId == 7); // left for the consumer to settle normally
    }

    [Fact]
    public async Task A_container_exited_beyond_the_grace_is_failed()
    {
        await StartRunAsync();
        _docker.NextSnapshot = new ContainerSnapshot("exited", 0, false, DateTime.UtcNow.AddMinutes(-5));

        await _watchdog.SweepAsync(CancellationToken.None);

        var failed = Assert.Single((await _fx.AppendedAsync()).OfType<RunFailed>());
        Assert.Contains("without a terminal callback", failed.Cause);
        Assert.Empty(await _store.LoadUnfinishedAsync());
    }

    [Fact]
    public async Task A_recorded_terminal_callback_is_never_raced_into_a_failure()
    {
        await StartRunAsync();
        await _log.AppendAsync(
            new AgentCallback { RunId = 7, Route = "completed", Body = """{"sessionId":"7"}""", EventId = "evt-7-terminal" },
            CancellationToken.None);
        _docker.NextSnapshot = new ContainerSnapshot("exited", 0, false, DateTime.UtcNow.AddMinutes(-5));

        await _watchdog.SweepAsync(CancellationToken.None);

        Assert.DoesNotContain(await _fx.AppendedAsync(), e => e is RunFailed);
        Assert.Contains(await _store.LoadUnfinishedAsync(), e => e.RunId == 7);
    }

    [Fact]
    public async Task A_terminal_callback_on_an_already_settled_run_is_skipped_without_events()
    {
        await _consumer.HandleAsync(
            new AgentCallback { RunId = 7, Route = "completed", Body = null, EventId = "evt-7-terminal" },
            attempt: 1, causationId: 0, CancellationToken.None);

        Assert.Empty(await _fx.AppendedAsync());
    }
}
