using System.Text.Json;

namespace PiAgent.RuntimeApi;

// ---- 04 §3: start request/response ----

public sealed record StartRunRequest(Guid AgentSessionId, Guid? WorkspaceId, Guid AgentProfileVersionId, JsonElement Input);

public sealed record StartRunResponse(
    Guid RunId,
    string RuntimeSessionId,
    string SessionFilePath,
    Guid ContainerInstanceId,
    string Status,
    string? KomodoDeploymentId = null,
    string? DeploymentName = null);

// ---- 04 §4: callbacks ----

public sealed record HeartbeatRequest(string EventId, DateTime OccurredAt);

public sealed record EventRequest(string EventId, string Type, DateTime OccurredAt, JsonElement? Payload);

/// <summary>Terminal callback (04 §4). `sessionFilePath` (ticket 12) is the per-Run session path the
/// Bridge discovered at launch (`get_state.sessionFile`, decision 57) — the Runtime API records it on
/// the AgentSession row; optional, absent when the agent did not report a path.</summary>
public sealed record CompletedRequest(string EventId, DateTime OccurredAt, string? Outcome, RunResult Result, string? SessionFilePath = null);

public sealed record RunResult(string Summary, RunResultArtifact[] Artifacts, string? Branch, string? CommitSha);

public sealed record RunResultArtifact(string Type, string Format, string Path);

public sealed record FailedRequest(string EventId, DateTime OccurredAt, FailedError Error, string? SessionFilePath = null);

public sealed record FailedError(string Code, string Message);

public sealed record StopRunRequest(string Reason);
