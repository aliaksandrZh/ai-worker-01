using PiAgent.Events;

namespace PiAgent.Publisher;

/// <summary>
/// The key-once guard (docs/publisher.md invariant 3): a marker is written in the same
/// transaction as the successful publication decision, so a redelivered event never
/// duplicates its artifact.
/// </summary>
public interface IPublishMarkerStore
{
    Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct);

    Task InsertAsync(string idempotencyKey, IEventTransaction? tx, CancellationToken ct);
}

/// <summary>Publisher configuration.</summary>
public sealed class PublisherOptions
{
    public const string SectionName = "Publisher";

    /// <summary>The only GitHub write credential in the system (mounted read-only secret).</summary>
    public string? PatFile { get; set; }
}
