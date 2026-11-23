using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Platform.Http;
using PiAgent.Platform.Workers;

namespace PiAgent.HostMonitor;

public sealed class HostMonitorOptions
{
    /// <summary>Plain directories on persistent host storage.</summary>
    public string ManagedRoot { get; set; } = "/var/lib/pi-agent/workflows";
}

/// <summary>Health check: managed storage root reachable/exists.</summary>
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

/// <summary>Baseline: storage + last-cycle observation counts.</summary>
public sealed class HostMonitorMetrics(IOptions<HostMonitorOptions> options, WorkerLoopState state, HostMonitorCycle? cycle)
    : WorkerMetricsBase(state)
{
    protected override IEnumerable<KeyValuePair<string, object?>> CollectExtra()
    {
        yield return new KeyValuePair<string, object?>("storageFreeBytes", TryFreeBytes());
        var last = cycle?.LastCycle;
        yield return new KeyValuePair<string, object?>("storageManagedUsageBytes", last is null ? null : HostMonitorCycle.DirectorySize(options.Value.ManagedRoot));
        yield return new KeyValuePair<string, object?>("containersObserved", last?.ContainersObserved);
        yield return new KeyValuePair<string, object?>("busyContainers", last?.BusyContainers);
        yield return new KeyValuePair<string, object?>("workflowDirectories", last?.WorkflowDirectories);
        // orphan decisions live in the Runtime API's orphan_candidate table (decision 77); the monitor
        // only reports the tree, so this gauge is the Runtime API's to publish
        yield return new KeyValuePair<string, object?>("reconciliationOrphanCandidateCount", null);
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
