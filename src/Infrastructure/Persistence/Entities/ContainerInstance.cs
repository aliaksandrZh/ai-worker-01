namespace PiAgent.Infrastructure.Persistence;

/// <summary>Orchestrator-managed desired state: `running` at preparation, `stopped` on Task
/// completion//`/stop`, `removed` at root closure.</summary>
public static class ContainerDesiredStates
{
    public const string Running = "running";
    public const string Stopped = "stopped";
    public const string Removed = "removed";
}

/// <summary>Observed states written back exclusively from Runtime API/Host-Monitor observations —
/// the Orchestrator never flips these without an observation.</summary>
public static class ContainerObservedStates
{
    public const string Unknown = "unknown";
    public const string Created = "created";
    public const string Running = "running";
    public const string Stopping = "stopping";
    public const string Stopped = "stopped";
    public const string Failed = "failed";
    public const string Removed = "removed";
}

/// <summary>
/// Tracks the desired vs observed machine of one Run's container. Created
/// `desired=running`/`observed=unknown` by the Orchestrator at Run preparation; observed state is only
/// written back by the Runtime API/Host Monitor. The container is replaceable and stopped after Task
/// completion; the stopped deployment may be recreated for a retry from the same persistent storage.
/// </summary>
public class ContainerInstance
{
    public Guid Id { get; set; }
    /// <summary>The Run's session owns the container; session deletion cascades.</summary>
    public Guid AgentSessionId { get; set; }
    /// <summary>The Run attempt this container executed; recreated per retry attempt.</summary>
    public Guid? RunId { get; set; }
    /// <summary>Runtime metadata written back after provisioning (the Docker container id).</summary>
    public string? DockerContainerId { get; set; }
    public string? ContainerName { get; set; }
    /// <summary>Exactly one configured execution host; records where the container was created.</summary>
    public string? HostId { get; set; }
    /// <summary>Pinned profile version's built image tag.</summary>
    public string? ContainerImage { get; set; }
    public string DesiredState { get; set; } = ContainerDesiredStates.Running;
    public string ObservedState { get; set; } = ContainerObservedStates.Unknown;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastStartedAt { get; set; }
    public DateTime? LastStoppedAt { get; set; }
    public DateTime? RemovedAt { get; set; }
}
