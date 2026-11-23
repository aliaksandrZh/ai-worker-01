using System.Text.Json;

namespace PiAgent.BuildWorker.Api;

// The build contract DTOs live in Platform.Contracts now (single shared copy, formerly this file
// mirrored the Orchestrator's own copy). This file keeps the Worker's own non-contract pieces:
// the durable-state/build-execution configuration and the local JSON options.

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
