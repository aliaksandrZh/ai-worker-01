namespace PiAgent.Platform.Http;

public sealed record ReadinessResult(bool Ok, string? Error = null)
{
    public static ReadinessResult Ready() => new(true);
    public static ReadinessResult NotReady(string error) => new(false, error);
}

/// <summary>One named dependency probe; /readyz aggregates every registered check.</summary>
public interface IReadinessCheck
{
    string Name { get; }
    Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken = default);
}
