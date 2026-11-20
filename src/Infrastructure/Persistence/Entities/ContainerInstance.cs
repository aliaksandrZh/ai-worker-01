namespace PiAgent.Infrastructure.Persistence;

/// <summary>Orchestrator-managed desired state (03 §5): `running` at preparation, `stopped` on Task
/// completion//`/stop`, `removed` at root closure (ticket 17).</summary>
public static class ContainerDesiredStates
{
    public const string Running = "running";
    public const string Stopped = "stopped";
    public const string Removed = "removed";
}

/// <summary>Observed states written back exclusively from Runtime API/Host-Monitor observations
/// (03 §5; tickets 09/13) — the Orchestrator never flips these without an observation.</summary>
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
/// Tracks the desired vs observed machine of one Run's container (02 §15, 03 §5). Created
/// `desired=running`/`observed=unknown` by the Orchestrator at Run preparation; observed state is only
/// written back by the Runtime API/Host Monitor. The container is replaceable and stopped after Task
/// completion; the stopped deployment may be recreated for a retry from the same persistent storage.
/// </summary>
public class ContainerInstance
{
    public Guid Id { get; set; }
    /// <summary>The Run's session owns the container (02 §15/ER); session deletion cascades.</summary>
    public Guid AgentSessionId { get; set; }
    /// <summary>The Run attempt this container executed; recreated per retry attempt.</summary>
    public Guid? RunId { get; set; }
    /// <summary>Runtime/Komodo metadata written back after provisioning (ticket 09).</summary>
    public string? KomodoDeploymentId { get; set; }
    public string? DeploymentName { get; set; }
    /// <summary>V1: exactly one configured execution host (decision 35); records where the container was created.</summary>
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
