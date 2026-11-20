using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Platform.Workers;

namespace PiAgent.BuildWorker;

/// <summary>§14 baseline: build metrics; build-job counters arrive with decision 81 (task 10+).</summary>
public sealed class BuildWorkerMetrics(WorkerLoopState state) : WorkerMetricsBase(state)
{
    protected override IEnumerable<KeyValuePair<string, object?>> CollectExtra()
    {
        yield return new KeyValuePair<string, object?>("buildsActive", 0);
    }
}
