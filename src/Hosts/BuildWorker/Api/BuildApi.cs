using PiAgent.Platform.Contracts;
using PiAgent.BuildWorker.Docker;
using PiAgent.BuildWorker.Builds;
using PiAgent.BuildWorker.Api;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PiAgent.Platform.Auth;

namespace PiAgent.BuildWorker.Api;

/// <summary>
/// The internal build API (04 §6, decision 81). Both endpoints are private-network and service-token
/// guarded — the Build Worker accepts the dedicated caller credential (`ServiceTokens__Orchestrator`,
/// decision 65; the same direction/shape as the Runtime API's task-09 contour). The Build Worker has
/// no database access whatsoever: idempotency and durability come from the local build-job documents.
/// </summary>
public static class BuildApi
{
    public static WebApplication MapBuildApi(this WebApplication app)
    {
        // POST /internal/builds — idempotent by buildId; immediate 202; never a duplicate build
        app.MapPost("/internal/builds", async (HttpRequest request, BuildDurableStore store, BuildChannel channel, ILogger<BuildApiLog> logger, CancellationToken cancellationToken) =>
        {
            BuildDispatchRequest? payload;
            try
            {
                var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
                payload = body.Deserialize<BuildDispatchRequest>(BuildJson.Default);
            }
            catch (JsonException)
            {
                return BuildApiErrors.BadRequest(BuildErrorCodes.BuildPayloadInvalid, "the request body is not a valid build dispatch payload");
            }
            if (payload is null)
                return BuildApiErrors.BadRequest(BuildErrorCodes.BuildPayloadInvalid, "the request body is not a valid build dispatch payload");

            var violation = Validate(payload);
            if (violation is { } message)
                return BuildApiErrors.BadRequest(BuildErrorCodes.BuildPayloadInvalid, message);

            var tag = $"agent-{payload.Profile}:v{payload.ProfileVersion}";
            var now = DateTime.UtcNow;

            if (store.Exists(payload.BuildId))
            {
                var existing = store.TryLoad(payload.BuildId)!;
                if (SameDispatch(existing, payload))
                    return Results.Json(ToResponse(existing), statusCode: StatusCodes.Status202Accepted);
                logger.LogWarning("build {buildId} redispatched with a different payload — refusing", payload.BuildId);
                return BuildApiErrors.Conflict(payload.BuildId);
            }

            var doc = new BuildDurableStore.BuildJobDoc
            {
                BuildId = payload.BuildId,
                Profile = payload.Profile,
                ProfileVersion = payload.ProfileVersion,
                SourceCommitSha = payload.SourceCommitSha,
                ConfigSnapshot = payload.ConfigSnapshot,
                BuildContextDirectory = payload.BuildContext?.Directory ?? "",
                State = BuildStates.Queued,
                ImageTag = tag,
                BuildLogReference = store.LogFile(payload.BuildId),
                CreatedAtUtc = now,
            };
            try
            {
                store.Insert(doc);
            }
            catch (BuildDurableStore.BuildAlreadyExistsException)
            {
                // a concurrent same-buildId dispatch won the insert race: serve that document instead
                var winner = store.TryLoad(payload.BuildId)!;
                if (!SameDispatch(winner, payload))
                    return BuildApiErrors.Conflict(payload.BuildId);
                return Results.Json(ToResponse(winner), statusCode: StatusCodes.Status202Accepted);
            }
            channel.Enqueue(doc.BuildId);
            logger.LogInformation("build {buildId} accepted: {tag} (context {context}, sourceCommit {commit})",
                doc.BuildId, tag, doc.BuildContextDirectory, doc.SourceCommitSha ?? "not pinned");
            return Results.Json(ToResponse(doc), statusCode: StatusCodes.Status202Accepted);
        }).RequireServiceToken();

        // GET /internal/builds/{buildId} — status and result; unknown buildId → 404
        app.MapGet("/internal/builds/{buildId:guid}", (Guid buildId, BuildDurableStore store) =>
        {
            var doc = store.TryLoad(buildId);
            if (doc is null)
                return Results.Json(new { code = BuildErrorCodes.BuildNotFound, message = $"no local build job for buildId '{buildId}'" },
                    statusCode: StatusCodes.Status404NotFound);
            return Results.Json(ToResponse(doc));
        }).RequireServiceToken();

        return app;
    }

    private static bool SameDispatch(BuildDurableStore.BuildJobDoc doc, BuildDispatchRequest payload) =>
        doc.Profile == payload.Profile
        && doc.ProfileVersion == payload.ProfileVersion
        && doc.SourceCommitSha == payload.SourceCommitSha
        && doc.ConfigSnapshot == payload.ConfigSnapshot
        && doc.BuildContextDirectory == (payload.BuildContext?.Directory ?? "");

    private static string? Validate(BuildDispatchRequest payload)
    {
        if (payload.BuildId == Guid.Empty)
            return "buildId is required";
        if (string.IsNullOrWhiteSpace(payload.Profile)
            || !payload.Profile.All(character => char.IsLower(character) || char.IsAsciiDigit(character) || character == '-' || character == '_'))
            return $"profile '{payload.Profile}' is not a valid lowercase profile name";
        if (payload.ProfileVersion <= 0)
            return "profileVersion must be positive";
        if (string.IsNullOrWhiteSpace(payload.ConfigSnapshot))
            return "configSnapshot is required";
        if (payload.BuildContext is null || string.IsNullOrWhiteSpace(payload.BuildContext.Directory))
            return "buildContext.directory is required";
        return null;
    }

    private static BuildResponse ToResponse(BuildDurableStore.BuildJobDoc doc) => new(
        doc.BuildId, doc.Profile, doc.ProfileVersion, doc.State, doc.ImageTag, doc.ImageId, doc.ImageDigest,
        doc.BuildLogReference, doc.ErrorCode, doc.ErrorMessage);
}

/// <summary>§7 error shape: { code, message, details }.</summary>
public sealed class BuildApiLog; // logger category for the build endpoints (static types cannot be type args)

public static class BuildApiErrors
{
    public static IResult BadRequest(string code, string message) =>
        Results.Json(new { code, message, details = (object?)null }, statusCode: StatusCodes.Status400BadRequest);

    public static IResult Conflict(Guid buildId) =>
        Results.Json(new
        {
            code = BuildErrorCodes.BuildPayloadConflict,
            message = $"buildId '{buildId}' already exists locally with a different dispatch payload",
            details = (object?)null,
        }, statusCode: StatusCodes.Status409Conflict);
}
