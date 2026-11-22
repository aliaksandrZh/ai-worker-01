namespace PiAgent.Infrastructure.Persistence;

/// <summary>
/// Execution Host Monitor telemetry (01 §2, decision 62): the Host Monitor observes session-file
/// activity, container state/processes/CPU and managed-storage usage on the execution host via the
/// local Docker API and posts it here with its own service credential (ticket 13). The Runtime API
/// persists the LATEST observation (single-row upsert — the Orchestrator's reconciliation reads only
/// current facts); the control plane never reads execution-host paths or process tables itself.
/// </summary>
public class MonitorObservation
{
    public Guid Id { get; set; }
    /// <summary>Host clock of the observation (sent by the monitor).</summary>
    public DateTime ObservedAt { get; set; }
    /// <summary>When the Runtime API persisted it.</summary>
    public DateTime ReceivedAt { get; set; }
    /// <summary>Per-container telemetry (label `pi-run`, state, cpu percent, process list, bind mounts) — jsonb.</summary>
    public string? Containers { get; set; }
    /// <summary>Managed-storage telemetry: top-level workflow directories with mtimes + container binds — jsonb.</summary>
    public string? Storage { get; set; }
    public DateTime UpdatedAt { get; set; }
}
