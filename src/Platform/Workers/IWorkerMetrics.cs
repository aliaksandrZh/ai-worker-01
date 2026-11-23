namespace PiAgent.Platform.Workers;

/// <summary>One unit of a worker's periodic cycle; every registered cycle runs on each tick.</summary>
public interface IWorkerCycle
{
    Task RunCycleAsync(CancellationToken cancellationToken);
}

public interface IWorkerMetrics
{
    IEnumerable<KeyValuePair<string, object?>> Collect();
}

/// <summary>Worker baseline metrics: running + heartbeat + last-successful-cycle, plus host extras.</summary>
public abstract class WorkerMetricsBase(WorkerLoopState state) : IWorkerMetrics
{
    public IEnumerable<KeyValuePair<string, object?>> Collect()
    {
        yield return new KeyValuePair<string, object?>("running", state.Running);
        yield return new KeyValuePair<string, object?>("heartbeatUtc", state.HeartbeatUtc);
        yield return new KeyValuePair<string, object?>("lastSuccessfulCycleUtc", state.LastSuccessfulCycleUtc);
        foreach (var pair in CollectExtra())
            yield return pair;
    }

    protected abstract IEnumerable<KeyValuePair<string, object?>> CollectExtra();
}
