using System.Text.Json;
using System.Text.Json.Serialization;
using PiAgent.Infrastructure.Persistence;

namespace PiAgent.Orchestrator.Builds;

// ---- 04 §6: the asynchronous build contract between the Orchestrator and the Build Worker ----
// The Orchestrator dispatches and polls; it never holds a connection open for the build itself
// (decision 81). DTO naming on the wire is explicit (camelCase) so both sides mirror 04 §6.

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
    [property: JsonPropertyName("buildContext")] BuildContextReference BuildContext);

/// <summary>Git directory of the profile inside the system repository (the `AgentProfileVersion.source_directory`).</summary>
public sealed record BuildContextReference([property: JsonPropertyName("directory")] string Directory);

/// <summary>`GET /internal/builds/{buildId}` response: build state; on completion the outcome.
/// Success carries the local image tag, image ID/digest and build-log reference; a real Docker build
/// failure is a terminal `failed` result. Unknown buildId is a 404.</summary>
public sealed record BuildStatusResponse(
    [property: JsonPropertyName("buildId")] Guid BuildId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("imageTag")] string? ImageTag,
    [property: JsonPropertyName("imageId")] string? ImageId,
    [property: JsonPropertyName("imageDigest")] string? ImageDigest,
    [property: JsonPropertyName("buildLogReference")] string? BuildLogReference,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage);

/// <summary>Build Worker local build states (task 10): queued → building → succeeded | failed (terminal).</summary>
public static class WorkerBuildStates
{
    public const string Queued = "queued";
    public const string Building = "building";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public static class BuildStatuses
{
    public static readonly JsonSerializerOptions PayloadJson = new() { WriteIndented = false };

    /// <summary>Local tag convention: `agent-<profile-name>:v<version>` (decisions 35/56/81).</summary>
    public static string ImageTagFor(string profileName, int version) => $"agent-{profileName}:v{version}";
}
