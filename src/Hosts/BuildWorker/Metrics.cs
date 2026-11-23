using PiAgent.BuildWorker.Docker;
using PiAgent.BuildWorker.Builds;
using PiAgent.BuildWorker.Api;
using PiAgent.Platform.Workers;

namespace PiAgent.BuildWorker;

/// <summary>§14 baseline: image builds, build errors, execution retries (worker heartbeat from the loop).</summary>
public sealed class BuildWorkerMetrics(WorkerLoopState state, RunnerCounters counters, BuildDurableStore store) : WorkerMetricsBase(state)
{
    protected override IEnumerable<KeyValuePair<string, object?>> CollectExtra()
    {
        // buildsActive: queued (awaiting the runner) + building (executing)
        var active = store.LoadAll().Count(doc => doc.State is BuildStates.Queued or BuildStates.Building);
        yield return new KeyValuePair<string, object?>("buildsActive", active);
        yield return new KeyValuePair<string, object?>("buildsSucceededTotal", counters.SucceededTotal);
        yield return new KeyValuePair<string, object?>("buildsFailedTotal", counters.FailedTotal);
        yield return new KeyValuePair<string, object?>("buildExecutionRetriesTotal", counters.ExecutionRetriesTotal);
    }
}
