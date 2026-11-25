using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PiAgent.Events.Delivery;

/// <summary>
/// The worker loop shared by every consumer (docs/eventlog.md): poll → claim a batch →
/// handle → ack / fail. Delivery semantics live here once, not per consumer.
/// </summary>
internal sealed class EventConsumerRunner<TConsumer>(
    TConsumer consumer,
    IEventQueue queue,
    IOptions<EventLogOptions> options,
    ILogger<EventConsumerRunner<TConsumer>> logger) : BackgroundService
    where TConsumer : IEventConsumer
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("event consumer {consumer} started for [{types}]",
            consumer.Name, string.Join(", ", consumer.Consumes.Select(EventRegistry.TypeName)));

        var types = consumer.Consumes.Select(EventRegistry.TypeName).ToArray();
        var interval = TimeSpan.FromSeconds(Math.Max(options.Value.PollIntervalSeconds, 1));

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = false;
            try
            {
                var batch = await queue.ClaimAsync(
                    types, options.Value.Batch,
                    TimeSpan.FromSeconds(options.Value.VisibilityTimeoutSeconds), stoppingToken);

                foreach (var claimed in batch)
                {
                    try
                    {
                        await consumer.HandleAsync(claimed.Event, claimed.Attempt, stoppingToken);
                        await queue.AckAsync(claimed.Id, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "consumer {consumer} failed {event} (attempt {attempt})",
                            consumer.Name, claimed.Event.GetType().Name, claimed.Attempt);
                        await queue.FailAsync(claimed.Id, ex.ToString(), options.Value.MaxAttempts, stoppingToken);
                    }

                    processed = true;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "consumer {consumer} claim cycle failed", consumer.Name);
            }

            if (!processed)
                await Task.Delay(interval, stoppingToken);
        }
    }
}
