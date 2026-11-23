using System.Text.Json.Serialization;

namespace PiAgent.Platform.Contracts;

// ---- 04 §6: the asynchronous build contract between the Orchestrator and the Build Worker ----
// The Orchestrator dispatches and polls; it never holds a connection open for the build itself
// (decision 81). DTO naming on the wire is explicit (camelCase via JsonPropertyName).
// Single shared copy in Platform: the former per-host mirrors had already drifted (class names and
// field sets differed — e.g. `BuildStatusResponse` vs `BuildResponse`); this is the union shape.
// Both sides read with System.Text.Json defaults (unknown members ignored), so extra fields on the
// response are harmless for the Orchestrator, and the Worker tolerates the absent buildContext.

/// <summary>`POST /internal/builds` payload: buildId, profile version reference, configuration snapshot,
/// source commit SHA, build context reference — all pinned on the AgentProfileVersion row.</summary>
public sealed record BuildDispatchRequest(
    [property: JsonPropertyName("buildId")] Guid BuildId,
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("profileVersion")] int ProfileVersion,
    [property: JsonPropertyName("sourceCommitSha")] string? SourceCommitSha,
    /// <summary>The immutable config_snapshot jsonb pinned on the version row (the Build Worker
    /// materializes its build context from the files inside it, never from a possibly-moved Git tree).</summary>
    [property: JsonPropertyName("configSnapshot")] string ConfigSnapshot,
    [property: JsonPropertyName("buildContext")] BuildContextReference? BuildContext);

/// <summary>Git directory of the profile inside the system repository (the `AgentProfileVersion.source_directory`).</summary>
public sealed record BuildContextReference([property: JsonPropertyName("directory")] string Directory);

/// <summary>`GET /internal/builds/{buildId}` (and the POST response body): build state; on completion
/// the outcome. Success carries the local image tag, image ID/digest and build-log reference; a real
/// Docker build failure is a terminal `failed` result. Unknown buildId is a 404.</summary>
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

/// <summary>Build states on the contract (task 10): queued → building → succeeded | failed (terminal).</summary>
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

/// <summary>Terminal error codes the Worker reports (recorded into the orchestrator's BuildJob.LastError).</summary>
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
