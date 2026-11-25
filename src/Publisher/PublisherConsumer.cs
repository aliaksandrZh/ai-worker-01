using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Events;
using PiAgent.Events.Delivery;

namespace PiAgent.Publisher;

/// <summary>
/// The publication consumer (docs/publisher.md): key-once, one atomic GitHub mutation
/// per event; transient failures throw (EventLog redelivery), permanent ones poison
/// straight to dead-letter.
/// </summary>
public sealed class PublisherConsumer(
    IPublishMarkerStore markers,
    IGithubWriteApi github,
    ILogger<PublisherConsumer> logger) : IEventConsumer
{
    public Type[] Consumes { get; } =
    [
        typeof(PublishComment), typeof(PublishPr), typeof(PublishReview), typeof(PublishBoardMove),
    ];

    public string Name => "Publisher";

    public async Task HandleAsync(IEvent evt, int attempt, CancellationToken ct)
    {
        switch (evt)
        {
            case PublishComment comment:
                await PublishOnceAsync(comment, comment.IdempotencyKey,
                    () => github.CreateCommentAsync(comment, ct), ct);
                break;

            case PublishPr pr:
                await PublishOnceAsync(pr, pr.IdempotencyKey,
                    () => github.CreateOrUpdatePrAsync(pr, ct), ct);
                break;

            case PublishReview review:
                await PublishOnceAsync(review, review.IdempotencyKey,
                    () => github.SubmitReviewAsync(review, ct), ct);
                break;

            case PublishBoardMove board:
                if (board.ItemNodeId.Length == 0 || board.ProjectNumber == 0)
                    return; // best-effort: nothing synced for this item
                await PublishOnceAsync(board, board.IdempotencyKey,
                    async () =>
                    {
                        await github.MoveBoardItemAsync(board, ct);
                        return 0L;
                    }, ct);
                break;
        }
    }

    private async Task PublishOnceAsync<TEvent>(
        TEvent evt, string idempotencyKey, Func<Task<long>> publish, CancellationToken ct)
        where TEvent : IEvent
    {
        if (await markers.ExistsAsync(idempotencyKey, ct))
        {
            logger.LogDebug("{event} already published under key {key}", typeof(TEvent).Name, idempotencyKey);
            return;
        }

        try
        {
            var artifactId = await publish();
            await markers.InsertAsync(idempotencyKey, null, ct);
            logger.LogInformation("{event} published under {key} → artifact {id}",
                typeof(TEvent).Name, idempotencyKey, artifactId);
        }
        catch (GithubPermanentException ex)
        {
            logger.LogError("permanent publication failure for {key}: {error}", idempotencyKey, ex.Message);
            throw new PermanentDeliveryException($"{ex.Message} (artifact state unknown — verify on GitHub before replay)");
        }
    }
}
