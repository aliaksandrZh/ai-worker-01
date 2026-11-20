using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Platform.Http;
using PiAgent.Platform.Workers;

namespace PiAgent.HostMonitor;

public sealed class HostMonitorOptions
{
    /// <summary>Decision 52: plain directories on persistent host storage.</summary>
    public string ManagedRoot { get; set; } = "/var/lib/pi-agent/workflows";
}

/// <summary>§13 health check: managed storage root reachable/exists.</summary>
public sealed class StorageReadinessCheck(IOptions<HostMonitorOptions> options) : IReadinessCheck
{
    public string Name => "storage";

    public Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Directory.Exists(options.Value.ManagedRoot)
            ? ReadinessResult.Ready()
            : ReadinessResult.NotReady($"managed storage root does not exist: {options.Value.ManagedRoot}"));
    }
}

/// <summary>§14 baseline: storage telemetry + reconciliation counters (placeholders until later tasks).</summary>
public sealed class HostMonitorMetrics(IOptions<HostMonitorOptions> options, WorkerLoopState state)
    : WorkerMetricsBase(state)
{
    protected override IEnumerable<KeyValuePair<string, object?>> CollectExtra()
    {
        yield return new KeyValuePair<string, object?>("storageFreeBytes", TryFreeBytes());
        yield return new KeyValuePair<string, object?>("storageManagedUsageBytes", null);
        yield return new KeyValuePair<string, object?>("reconciliationDesiredObservedMismatchCount", 0L);
        yield return new KeyValuePair<string, object?>("reconciliationOrphanCandidateCount", 0L);
    }

    private long? TryFreeBytes()
    {
        try
        {
            return new DriveInfo(options.Value.ManagedRoot).AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }
}
