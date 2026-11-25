namespace PiAgent.Events.Delivery;

/// <summary>docs/eventlog.md "Configuration knobs" — delivery tuning, never part of the interface.</summary>
public sealed class EventLogOptions
{
    public const string SectionName = "EventLog";

    /// <summary>Postgres connection string (ConnectionStrings:DefaultDb by convention).</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Poll cadence for all consumers (seconds).</summary>
    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>How long a claimed event stays invisible to other claims (seconds).</summary>
    public int VisibilityTimeoutSeconds { get; set; } = 300;

    /// <summary>Deliveries before an event moves to the dead-letter store.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Events claimed per loop iteration.</summary>
    public int Batch { get; set; } = 10;
}
