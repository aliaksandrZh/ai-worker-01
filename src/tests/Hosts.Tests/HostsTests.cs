using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PiAgent.BuildWorker;
using PiAgent.HostMonitor;
using PiAgent.Orchestrator;
using PiAgent.Orchestrator.Commands;
using PiAgent.OutboxWorker;
using PiAgent.Poller;
using PiAgent.Platform.Workers;
using PiAgent.RuntimeApi;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PiAgent.Hosts.Tests;

public class RuntimeApiEndpointsTests : IClassFixture<WebApplicationFactory<KomodoReadinessCheck>>
{
    private readonly WebApplicationFactory<KomodoReadinessCheck> _factory;

    public RuntimeApiEndpointsTests(WebApplicationFactory<KomodoReadinessCheck> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DefaultDb", "Host=127.0.0.1;Database=test;Username=test;Password=test");
            builder.UseSetting("Komodo:Url", "http://127.0.0.1:9");
        });
    }

    [Fact]
    public async Task Readyz_Returns503_AndNamesKomodo_WhenKomodoUnreachable()
    {
        var response = await _factory.CreateClient().GetAsync("/readyz");

        Assert.Equal(503, (int)response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var failed = doc.RootElement.GetProperty("failed");
        Assert.Contains(failed.EnumerateArray(), f => f.GetProperty("name").GetString() == "komodo");
    }

    [Fact]
    public async Task Metrics_ContainDbSizeBytesKey_EvenWhenDbUnreachable()
    {
        var response = await _factory.CreateClient().GetAsync("/metrics");

        Assert.Equal(200, (int)response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("runtime-api", doc.RootElement.GetProperty("service").GetString());
        var metrics = doc.RootElement.GetProperty("metrics");
        Assert.True(metrics.TryGetProperty("dbSizeBytes", out _));
    }

    [Fact]
    public void DbSizeCycle_IsRegisteredAsWorkerCycle_AndSharedInstanceWithMetrics()
    {
        // The worker loop runs only registered IWorkerCycle services; DbSizeCycle must be one of
        // them, and /metrics must read from the same instance the loop refreshes.
        var cycles = _factory.Services.GetServices<Platform.Workers.IWorkerCycle>().ToList();
        Assert.Contains(cycles, cycle => cycle is DbSizeCycle);
        Assert.Same(_factory.Services.GetRequiredService<DbSizeCycle>(), cycles.Single(c => c is DbSizeCycle));
    }
}

/// <summary>Each worker's /metrics key set per Global Constraints (§14) — null placeholders until later tasks.</summary>
public class WorkerMetricKeyTests
{
    private static WorkerLoopState State() => new();

    [Fact]
    public void OrchestratorMetrics_HaveQueueKeys()
    {
        var collect = new OrchestratorMetrics(State(), new CommandMetrics()).Collect().ToDictionary(p => p.Key, p => p.Value);
        Assert.True(collect.ContainsKey("queuePendingJobs"));
        Assert.True(collect.ContainsKey("queueOldestJobAgeSeconds"));
        Assert.True(collect.ContainsKey("queueClaimWaitSeconds"));
        Assert.Null(collect["queuePendingJobs"]);
        Assert.Equal(0L, collect["commandsRejectedTotal"]);
    }

    [Fact]
    public void PollerMetrics_HavePollerKeys()
    {
        var collect = new PollerMetrics(State(), new PollerState()).Collect().ToDictionary(p => p.Key, p => p.Value);
        Assert.True(collect.ContainsKey("pollerLagSeconds"));
        Assert.True(collect.ContainsKey("pollerLastSuccessUtc"));
        Assert.True(collect.ContainsKey("pollerLastCycleDurationSeconds"));
        Assert.True(collect.ContainsKey("pollerErrorsTotal"));
        Assert.True(collect.ContainsKey("pollerRateLimitRemaining"));
    }

    [Fact]
    public void OutboxMetrics_HaveOutboxKeys()
    {
        var collect = new OutboxMetrics(State(), new OutboxState()).Collect().ToDictionary(p => p.Key, p => p.Value);
        Assert.True(collect.ContainsKey("outboxPending"));
        Assert.True(collect.ContainsKey("outboxFailed"));
        Assert.True(collect.ContainsKey("outboxDeadLetter"));
        Assert.Equal(0L, collect["outboxPending"]);
    }

    [Fact]
    public void HostMonitorMetrics_HaveStorageAndReconciliationKeys()
    {
        var collect = new HostMonitorMetrics(
                Microsoft.Extensions.Options.Options.Create(new HostMonitorOptions { ManagedRoot = Path.GetTempPath() }),
                State())
            .Collect().ToDictionary(p => p.Key, p => p.Value);
        Assert.True(collect.ContainsKey("storageFreeBytes"));
        Assert.True(collect.ContainsKey("storageManagedUsageBytes"));
        Assert.True(collect.ContainsKey("reconciliationDesiredObservedMismatchCount"));
        Assert.True(collect.ContainsKey("reconciliationOrphanCandidateCount"));
        Assert.True((long?)collect["storageFreeBytes"] > 0);
    }

    [Fact]
    public void BuildWorkerMetrics_HaveBuilsActiveKey()
    {
        var collect = new BuildWorkerMetrics(State()).Collect().ToDictionary(p => p.Key, p => p.Value);
        Assert.True(collect.ContainsKey("buildsActive"));
        Assert.Equal(0, collect["buildsActive"]);
    }
}
