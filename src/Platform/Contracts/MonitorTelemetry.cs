namespace PiAgent.Platform.Contracts;

// ---- 04 §4: the Execution Host Monitor → Runtime API telemetry contract (decision 62, ticket 13) ----
// Single shared copy: both hosts previously held identical local record definitions that jscpd flagged
// as a clone; any future field addition now happens once. The Monitor serializes with PostAsJsonAsync
// (System.Text.Json Web defaults, camelCase); the Runtime API deserializes the same way — no attribute
// needed as long as both sides keep the Web defaults.

/// <summary>`POST /internal/runtime/monitor/telemetry` payload: one observation per post, the Runtime API
/// upserts it as the single latest row.</summary>
public sealed record MonitorTelemetryRequest(DateTime ObservedAtUtc, MonitorContainerTelemetry[] Containers, MonitorStorageTelemetry? Storage);

public sealed record MonitorContainerTelemetry(
    string? ContainerId, string? Name, Guid? RunId, Guid? TaskId, string? State,
    double? CpuPercent, int? ProcessCount, string[]? Processes, string[]? Binds, bool Busy, string? Note);

public sealed record MonitorStorageTelemetry(
    string Root, long? FreeBytes, long? UsedBytes, MonitorWorkflowDirTelemetry[] Directories);

public sealed record MonitorWorkflowDirTelemetry(string Name, DateTime ModifiedAtUtc, long? SizeBytes);
