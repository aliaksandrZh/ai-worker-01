using PiAgent.Platform.Contracts;
using PiAgent.BuildWorker.Docker;
using PiAgent.BuildWorker.Builds;
using PiAgent.BuildWorker.Api;
using Microsoft.Extensions.Logging;
using PiAgent.Platform.Workers;

namespace PiAgent.BuildWorker.Builds;

/// <summary>
/// §13 worker-cycle safety net (runs on the Platform worker loop, 5 s tick): re-enqueues `queued`
/// documents that have no wake-up pending — this closes the POST-crash window (document persisted,
/// process died before the runner picked it up; restart rescan covers it, the sweep covers a runner
/// that lost a message without a restart). `building` documents are the runner's alone; a stranded
/// one only exists when the process died, which is the runner's startup resume.
/// </summary>
public sealed class BuildJobSweep(BuildDurableStore store, BuildChannel channel, ILogger<BuildJobSweep> logger) : IWorkerCycle
{
    public Task RunCycleAsync(CancellationToken cancellationToken)
    {
        foreach (var doc in store.LoadAll())
        {
            if (doc.State != BuildStates.Queued)
                continue;
            channel.Enqueue(doc.BuildId);
            logger.LogDebug("sweep re-enqueued queued build {buildId}", doc.BuildId);
        }
        return Task.CompletedTask;
    }
}
