namespace PiAgent.Orchestrator.Orchestration;

/// <summary>
/// Queue worker knobs. No YAML knobs exist for these in the V1 configuration (spec defines none for the
/// claim lease); defaults are code constants, overridable per-host via configuration if ever needed —
/// a YAML knob is a separate approved config-plane change.
/// </summary>
public sealed class JobQueueOptions
{
    /// <summary>Claim lease width. The lease covers claim → rechecks → dispatch handoff; a crashed
    /// claim is recovered after this window (ticket 09's reconciliation continues from there).</summary>
    public int LeaseSeconds { get; set; } = 300;

    /// <summary>Max jobs claimed per tick.</summary>
    public int BatchLimit { get; set; } = 16;
}
