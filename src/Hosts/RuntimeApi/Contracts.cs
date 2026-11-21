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

public sealed record CompletedRequest(string EventId, DateTime OccurredAt, string? Outcome, RunResult Result);

public sealed record RunResult(string Summary, RunResultArtifact[] Artifacts, string? Branch, string? CommitSha);

public sealed record RunResultArtifact(string Type, string Format, string Path);

public sealed record FailedRequest(string EventId, DateTime OccurredAt, FailedError Error);

public sealed record FailedError(string Code, string Message);

public sealed record StopRunRequest(string Reason);
