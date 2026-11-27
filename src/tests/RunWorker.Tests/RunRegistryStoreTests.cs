using Microsoft.Extensions.Logging.Abstractions;
using PiAgent.Events;
using PiAgent.RunWorker;
using PiAgent.Tests.Postgres;
using Xunit;

namespace PiAgent.RunWorker.Tests;

/// <summary>
/// The durable registry side (`run_container`): the provisioning fact roundtrip, the
/// restart paths — re-admission by token and recovery of in-flight rows — and the
/// settle fact. Schema from the real store DDL (RunContainerSchema.EnsureAsync).
/// </summary>
[Collection("PostgresLog")]
public sealed class RunRegistryStoreTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly RunRegistryStore _store;

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
        await RunContainerSchema.SeedRunAsync(_fx, runId: 7, taskId: 3, input: InputJson());
    }

    public RunRegistryStoreTests(PostgresFixture fx)
    {
        _fx = fx;
        _store = RunContainerSchema.Store(fx);
    }

    private static RunEntry Entry(long runId = 7, string token = "tok-7") => new(
        RunId: runId, TaskId: 3, WorkflowId: 2,
        Input: new RunInput("o", "r", 1, PrNumber: null, Command: "implement",
            PlanCommentId: 0, Profile: "coder", AgentBranch: "issue-7",
            DefaultBranch: "main", ReviewHeadSha: null),
        ContainerName: "pi-session-7", Token: token, StartedUtc: DateTime.UtcNow);

    private static string InputJson() =>
        System.Text.Json.JsonSerializer.Serialize(Entry().Input, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    [Fact]
    public async Task The_provisioning_fact_roundtrips_and_re_admits_by_token()
    {
        var entry = Entry();
        await _store.InsertAsync(entry, containerId: "docker-abc", CancellationToken.None);

        var byToken = await _store.GetByTokenAsync("tok-7", CancellationToken.None);
        Assert.NotNull(byToken);
        Assert.Equal(entry.RunId, byToken.RunId);
        Assert.Equal(entry.Input.Profile, byToken.Input.Profile);
        Assert.Equal("docker-abc", byToken.ContainerName); // the docker id is the handle

        var unfinished = await _store.LoadUnfinishedAsync(CancellationToken.None);
        Assert.Equal([entry with { ContainerName = "docker-abc" }], unfinished);
    }

    [Fact]
    public async Task A_finished_run_is_no_longer_in_flight_nor_re_admitted()
    {
        await _store.InsertAsync(Entry(), "docker-abc", CancellationToken.None);
        await _store.MarkFinishedAsync(7, CancellationToken.None);
        await _store.MarkFinishedAsync(7, CancellationToken.None); // idempotent

        Assert.Null(await _store.GetByTokenAsync("tok-7", CancellationToken.None));
        Assert.Empty(await _store.LoadUnfinishedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_tokens_and_missing_rows_answer_null()
    {
        Assert.Null(await _store.GetByTokenAsync("nobody", CancellationToken.None));
        await _store.MarkFinishedAsync(999, CancellationToken.None); // no row — a silent no-op
    }

    [Fact]
    public async Task An_in_flight_run_is_addressable_by_id_until_it_finishes()
    {
        await _store.InsertAsync(Entry(), "docker-abc", CancellationToken.None);
        Assert.NotNull(await _store.GetAsync(7, CancellationToken.None));

        await _store.MarkFinishedAsync(7, CancellationToken.None);

        Assert.Null(await _store.GetAsync(7, CancellationToken.None));
    }
}
