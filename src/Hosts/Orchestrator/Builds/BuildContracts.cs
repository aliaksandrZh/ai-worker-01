using System.Text.Json;
using PiAgent.Platform.Contracts;

namespace PiAgent.Orchestrator.Builds;

// ---- the build contract DTOs live in Platform.Contracts now (single shared copy, formerly a
// per-host mirror of the Build Worker's own file). This file keeps only the Orchestrator's helpers
// around the contract: the dispatch-payload JSON options and the local image-tag convention.

public static class BuildStatuses
{
    public static readonly JsonSerializerOptions PayloadJson = new() { WriteIndented = false };

    /// <summary>Local tag convention: `agent-<profile-name>:v<version>`.</summary>
    public static string ImageTagFor(string profileName, int version) => $"agent-{profileName}:v{version}";
}
