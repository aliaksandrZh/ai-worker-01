using PiAgent.BuildWorker.Docker;
using PiAgent.BuildWorker.Builds;
using PiAgent.BuildWorker.Api;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PiAgent.BuildWorker.Builds;

/// <summary>
/// Single-consumer build executor (04 §6 contract, worker side). Startup resume (the restart reconciler):
/// unfinished documents are finalized when their pinned image tag already exists (a daemon build that
/// completed while the worker was down) or re-enqueued for an idempotent rebuild from the pinned
/// snapshot — the same `buildId`, no duplicate orchestration. Terminal results are durable documents:
/// results stay retrievable after worker restarts or network failures.
/// Execution model per document:
/// - pinned context materialized (hash-verified) and tared → docker build (progress streamed into the
///   durable build log) → image inspect; tag existence is the authoritative result.
/// - daemon build errors → terminal `failed` (`docker_build_failed`); pinned-context problems → terminal
///   `build_context_invalid`; ambiguous execution failures (transport/timeout) retry up to
///   `MaxExecutionAttempts`, then terminal `failed` (`build_execution_failed`) so nothing strands.
/// The docker daemon cancels the build when this process dies, so restarts either finalize on the tag
/// or rebuild — never leave a half-built phantom.
/// </summary>
public sealed class BuildJobRunner(
    BuildDurableStore store,
    IDockerEngine engine,
    BuildChannel channel,
    RunnerCounters counters,
    IOptions<BuildOptions> options,
    ILogger<BuildJobRunner> logger) : BackgroundService
{
    private readonly BuildOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ResumeAsync(cancellationToken);
            await foreach (var buildId in channel.ReadAllAsync(cancellationToken))
            {
                // The durable document is the single source of truth — a duplicate entry, a lost id, or
                // a terminal one is a no-op here. The queue is only a wake-up signal.
                await ExecuteOneAsync(buildId, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        foreach (var unfinished in store.LoadAll().Where(doc => doc.State is BuildStates.Queued or BuildStates.Building))
        {
            var image = await InspectQuietlyAsync(unfinished.ImageTag, cancellationToken);
            if (image is { } finalized)
            {
                MarkSucceeded(unfinished, finalized);
                logger.LogInformation("resumed build {buildId}: image {tag} already exists — finalized as succeeded", unfinished.BuildId, unfinished.ImageTag);
                continue;
            }
            if (unfinished.ExecutionAttempts >= _options.MaxExecutionAttempts)
            {
                MarkFailed(unfinished, BuildFailureCodes.BuildExecutionFailed,
                    $"build execution failed after {unfinished.ExecutionAttempts} attempts (last: {unfinished.ExecutionLastError ?? "unknown"})");
                logger.LogWarning("resumed build {buildId}: attempts exhausted — failed", unfinished.BuildId);
                continue;
            }
            channel.Enqueue(unfinished.BuildId); // the durable doc is the truth; the enqueue is only a wake-up
            logger.LogInformation("resumed unfinished build {buildId} ({state}) — re-enqueued (attempt {attempt}/{max})",
                unfinished.BuildId, unfinished.State, unfinished.ExecutionAttempts + 1, _options.MaxExecutionAttempts);
        }
    }

    private async Task ExecuteOneAsync(Guid buildId, CancellationToken cancellationToken)
    {
        var doc = store.TryLoad(buildId);
        if (doc is null || doc.State is BuildStates.Succeeded or BuildStates.Failed)
            return;

        if (doc.State != BuildStates.Building)
        {
            doc.State = BuildStates.Building;
            doc.StartedAtUtc ??= DateTime.UtcNow;
            store.Save(doc);
        }

        var contextDirectory = store.ContextDirectory(doc.BuildId);
        var logPath = store.LogFile(doc.BuildId);

        // stage 1: pinned build context (terminal on any inconsistency; logged into the durable build log)
        byte[] contextTar;
        try
        {
            BuildContextComposer.Compose(contextDirectory, doc.ConfigSnapshot);
            contextTar = TarBuilder.Create(contextDirectory);
        }
        catch (BuildContextException exception)
        {
            MarkFailed(doc, BuildFailureCodes.BuildContextInvalid, exception.Message);
            LogTerminal(doc, BuildFailureCodes.BuildContextInvalid, exception.Message);
            counters.FailedTotal++;
            return;
        }
        catch (Exception exception)
        {
            var message = $"materializing or taring the pinned build context failed: {exception.Message}";
            MarkFailed(doc, BuildFailureCodes.BuildContextInvalid, message);
            LogTerminal(doc, BuildFailureCodes.BuildContextInvalid, message);
            counters.FailedTotal++;
            return;
        }

        // the pinned tag from dispatch — always present (insert-time invariant)
        var tag = doc.ImageTag ?? throw new BuildContextException("the durable build document carries no image tag");

        AppendLog(logPath, $"== docker build {tag} (context {doc.BuildContextDirectory}, pinned via config_snapshot, {contextTar.Length}-byte tar) ==");

        // stage 2: the docker build; tag existence after the build stream is the authoritative result
        try
        {
            using var execution = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            execution.CancelAfter(TimeSpan.FromMinutes(_options.BuildTimeoutMinutes));
            await engine.BuildAsync(tag, contextTar, logPath, execution.Token);
            var image = await engine.InspectImageAsync(tag, execution.Token)
                ?? throw new DockerBuildException($"docker build reported success but image '{tag}' is absent from the local image store");
            MarkSucceeded(doc, image);
            LogTerminal(doc, BuildStates.Succeeded, $"{tag} ({image.ImageId})");
            counters.SucceededTotal++;
            logger.LogInformation("build {buildId} succeeded: {tag} ({id})", doc.BuildId, doc.ImageTag, image.ImageId);
        }
        catch (BuildContextException exception)
        {
            MarkFailed(doc, BuildFailureCodes.BuildContextInvalid, exception.Message);
            LogTerminal(doc, BuildFailureCodes.BuildContextInvalid, exception.Message);
            counters.FailedTotal++;
        }
        catch (DockerBuildException exception)
        {
            MarkFailed(doc, BuildFailureCodes.DockerBuildFailed, exception.Message);
            LogTerminal(doc, BuildFailureCodes.DockerBuildFailed, exception.Message);
            counters.FailedTotal++;
            logger.LogWarning("build {buildId} failed: {message}", doc.BuildId, exception.Message);
        }
        catch (BuildExecutionException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // ambiguous (daemon unreachable / transport / client timeout): the same buildId retries,
            // bounded so nothing strands in `building`
            doc.ExecutionAttempts++;
            doc.ExecutionLastError = exception.Message;
            store.Save(doc);
            if (doc.ExecutionAttempts >= _options.MaxExecutionAttempts)
            {
                MarkFailed(doc, BuildFailureCodes.BuildExecutionFailed,
                    $"build execution failed after {doc.ExecutionAttempts} attempts (last: {exception.Message})");
                LogTerminal(doc, BuildFailureCodes.BuildExecutionFailed, exception.Message);
                counters.FailedTotal++;
            }
            else
            {
                counters.ExecutionRetriesTotal++;
                logger.LogWarning("build {buildId} execution failed (attempt {attempt}/{max}): {message} — retrying in {delay}s",
                    doc.BuildId, doc.ExecutionAttempts, _options.MaxExecutionAttempts, exception.Message, _options.ExecutionRetryDelaySeconds);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.ExecutionRetryDelaySeconds), CancellationToken.None);
                    channel.Enqueue(doc.BuildId);
                });
            }
        }
    }

    private void AppendLog(string logPath, string line)
    {
        var directory = Path.GetDirectoryName(logPath)!;
        Directory.CreateDirectory(directory);
        using var writer = new StreamWriter(logPath, append: true) { AutoFlush = true };
        writer.WriteLine(line);
    }

    private void LogTerminal(BuildDurableStore.BuildJobDoc doc, string code, string message)
        => AppendLog(store.LogFile(doc.BuildId), $"== terminal: {code}: {message} ==");

    private async Task<EngineImageInfo?> InspectQuietlyAsync(string? tag, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        try
        {
            return await engine.InspectImageAsync(tag, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "image inspect for {tag} failed — treating the image as absent", tag);
            return null;
        }
    }

    private void MarkSucceeded(BuildDurableStore.BuildJobDoc doc, EngineImageInfo image)
    {
        doc.State = BuildStates.Succeeded;
        doc.ImageId = image.ImageId;
        doc.ImageDigest = image.ImageId; // local image store: the image ID is the digest reference in V1
        doc.CompletedAtUtc = DateTime.UtcNow;
        doc.ErrorCode = null;
        doc.ErrorMessage = null;
        store.Save(doc);
    }

    private void MarkFailed(BuildDurableStore.BuildJobDoc doc, string errorCode, string message)
    {
        doc.State = BuildStates.Failed;
        doc.CompletedAtUtc = DateTime.UtcNow;
        doc.ErrorCode = errorCode;
        doc.ErrorMessage = message;
        store.Save(doc);
    }
}

/// <summary>§14 runtime counters shared between the runner and the metrics endpoint.</summary>
public sealed class RunnerCounters
{
    public long SucceededTotal;
    public long FailedTotal;
    public long ExecutionRetriesTotal;
}
