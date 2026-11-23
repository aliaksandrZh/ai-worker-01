using PiAgent.BuildWorker.Docker;
using PiAgent.BuildWorker.Builds;
using PiAgent.BuildWorker.Api;
using System.Threading.Channels;

namespace PiAgent.BuildWorker.Builds;

/// <summary>
/// The in-process build queue: one build executes at a time (the Build Job Runner is a single
/// consumer). POST inserts the durable document and enqueues the buildId here; the sweep cycle
/// re-enqueues stranded `queued` documents. Correctness never depends on the queue: duplicate
/// entries or a consumed-but-lost id are no-ops against the durable document state.
/// </summary>
public sealed class BuildChannel
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    /// <summary>Enqueues a buildId for execution; a duplicate entry is a harmless no-op (the runner
    /// re-reads the durable document and skips terminal/absent ones).</summary>
    public void Enqueue(Guid buildId) => _channel.Writer.TryWrite(buildId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
