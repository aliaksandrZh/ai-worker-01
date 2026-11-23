using PiAgent.BuildWorker.Docker;
using PiAgent.BuildWorker.Builds;
using PiAgent.BuildWorker.Api;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiAgent.BuildWorker.Api;

// ---- 04 §6: the Build Worker's internal build API (decision 81) ----
// DTOs are this service's own mirror of the contract; the Orchestrator holds its copy. camelCase on the wire.

/// <summary>`POST /internal/builds` payload (mirrors the Orchestrator's BuildDispatchRequest): idempotent
/// by `buildId` — an existing in-flight or finished build is returned as-is, never duplicated.</summary>
public sealed record BuildDispatchRequest(
    [property: JsonPropertyName("buildId")] Guid BuildId,
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("profileVersion")] int ProfileVersion,
    [property: JsonPropertyName("sourceCommitSha")] string? SourceCommitSha,
    /// <summary>The immutable AgentProfileVersion.config_snapshot jsonb (04 §6: configuration snapshot);
    /// its file list + contents are the pinned build context.</summary>
    [property: JsonPropertyName("configSnapshot")] string ConfigSnapshot,
    [property: JsonPropertyName("buildContext")] BuildContextReference? BuildContext);

public sealed record BuildContextReference([property: JsonPropertyName("directory")] string Directory);

/// <summary>`GET /internal/builds/{buildId}` (and the POST response body): build state; success carries
/// the local image tag, image ID/digest and build-log reference; a real Docker build failure is a
/// terminal `failed` result. Unknown buildId → 404.</summary>
public sealed record BuildResponse(
    [property: JsonPropertyName("buildId")] Guid BuildId,
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("profileVersion")] int ProfileVersion,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("imageTag")] string? ImageTag,
    [property: JsonPropertyName("imageId")] string? ImageId,
    [property: JsonPropertyName("imageDigest")] string? ImageDigest,
    [property: JsonPropertyName("buildLogReference")] string? BuildLogReference,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage);

/// <summary>Worker-local build states: accepted → queued → building → succeeded | failed (terminal).</summary>
public static class BuildStates
{
    public const string Queued = "queued";
    public const string Building = "building";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

/// <summary>§8 error additions for the build contract (documented in docs/tasks/10-build-worker.md).</summary>
public static class BuildErrorCodes
{
    public const string BuildNotFound = "build_not_found";
    public const string BuildPayloadConflict = "build_payload_conflict";
    public const string BuildPayloadInvalid = "build_payload_invalid";
}

/// <summary>Terminal error codes the Worker reports (mirrored into the orchestrator's BuildJob.LastError).</summary>
public static class BuildFailureCodes
{
    /// <summary>The Docker build itself failed (a real build failure → version `build_failed`).</summary>
    public const string DockerBuildFailed = "docker_build_failed";
    /// <summary>The pinned build context is unusable (missing Dockerfile/profile.yaml, hash mismatch).</summary>
    public const string BuildContextInvalid = "build_context_invalid";
    /// <summary>Pathological execution failure (transport/timeout) after all retries; distinct so
    /// "daemon build failed" stays separable from "worker lost contact" in the recorded result.</summary>
    public const string BuildExecutionFailed = "build_execution_failed";
}

/// <summary>Config: the worker's durable state root + build-execution knobs (compose: `Build__*`).</summary>
public sealed class BuildOptions
{
    /// <summary>Durable build-job state — one JSON document per buildId plus build logs and pinned materialized contexts.</summary>
    public string StateRoot { get; set; } = "/var/lib/pi-agent/builds";
    /// <summary>Client-side cap on one docker build execution (the daemon build dies with the request).</summary>
    public int BuildTimeoutMinutes { get; set; } = 30;
    /// <summary>Retries reserved for ambiguous execution failures (transport/timeout), not for `docker_build_failed`.</summary>
    public int MaxExecutionAttempts { get; set; } = 3;
    public int ExecutionRetryDelaySeconds { get; set; } = 20;
}

public static class BuildJson
{
    public static readonly JsonSerializerOptions Default = new() { WriteIndented = false };
}
