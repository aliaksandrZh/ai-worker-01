namespace PiAgent.Platform;

/// <summary>Correlation identifiers. Absent = null.</summary>
public sealed class CorrelationContext
{
    public string? CorrelationId { get; set; }
    public string? RepositoryId { get; set; }
    public string? WorkflowId { get; set; }
    public string? TaskId { get; set; }
    public string? RunId { get; set; }
    public string? EventId { get; set; }
}

/// <summary>Ambient correlation scope; flows through async contexts (request handlers, RPC).</summary>
public static class CorrelationAccessor
{
    private static readonly AsyncLocal<CorrelationContext?> _current = new();

    public static CorrelationContext? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
