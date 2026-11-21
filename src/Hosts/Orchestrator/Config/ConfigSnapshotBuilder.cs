using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiAgent.Orchestrator.Config;

/// <summary>Immutable pinned snapshot for one profile version: normalized profile.yaml + files (hash + contents).</summary>
public sealed record ProfileSnapshot(string ProfileName, string SourceDirectory, string SnapshotJson, string ConfigHash);

/// <summary>
/// Builds the immutable AgentProfileVersion.config_snapshot (02 §12, decision 54):
/// normalized profile configuration, per-file sha256 AND file contents (owner ruling — the version stays
/// rebuildable without the Git repo; source_commit_sha is optional traceability metadata only).
/// config_hash is sha256 over the canonical one-line snapshot JSON; JSON member order is fixed by DTO
/// declaration and file lists are sorted, so identical input ⇒ identical snapshot and hash.
/// </summary>
public static class ConfigSnapshotBuilder
{
    private const long MaxSnapshotBytes = 1024 * 1024;
    private const string LimitText = "1 MiB";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ProfileSnapshot Build(ProfileConfig profile, string profileDirectory, string? sourceCommitSha)
    {
        if (!Directory.Exists(profileDirectory))
            throw new ConfigException($"profile '{profile.Name}': directory '{profileDirectory}' does not exist");

        var fileDtos = new List<FileEntryDto>();
        var contents = new SortedDictionary<string, string>();
        long totalBytes = 0;

        foreach (var absolutePath in Directory.EnumerateFiles(profileDirectory, "*", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(profileDirectory, absolutePath).Replace('\\', '/');
            var bytesToHash = File.ReadAllBytes(absolutePath);
            totalBytes += bytesToHash.Length;
            fileDtos.Add(new FileEntryDto(
                relativePath,
                Convert.ToHexString(SHA256.HashData(bytesToHash)).ToLowerInvariant()));
            contents[relativePath] = File.ReadAllText(absolutePath);
        }

        if (totalBytes > MaxSnapshotBytes)
            throw new ConfigException(
                $"profile '{profile.Name}': directory payload exceeds the {LimitText} snapshot limit ({totalBytes} bytes)");

        var dto = new SnapshotDto
        {
            Profile = profile.Name,
            SourceDirectory = $"config/agents/{profile.Name}",
            SourceCommitSha = sourceCommitSha,
            RuntimeType = profile.RuntimeType,
            Description = profile.Description,
            Enabled = profile.Enabled,
            ProfileYaml = new ProfileYamlDto
            {
                Description = profile.Description,
                Enabled = profile.Enabled,
                RuntimeType = profile.RuntimeType,
                MaxContextTokens = profile.MaxContextTokens,
                StartupTimeout = profile.StartupTimeout,
                IdleTimeout = profile.IdleTimeout,
                MaxRunDuration = profile.MaxRunDuration,
                Cpu = profile.Cpu,
                Memory = profile.Memory,
                MaxAutomaticRetries = profile.MaxAutomaticRetries,
                InitialBackoff = profile.InitialBackoff,
                BridgeVersion = profile.BridgeVersion,
                BridgeHeartbeatInterval = profile.BridgeHeartbeatInterval,
                BridgeEventCadence = profile.BridgeEventCadence,
                BridgeBehavior = profile.BridgeBehavior,
                SystemPrompt = profile.SystemPrompt,
                Skills = profile.Skills,
                Secrets = profile.Secrets,
                Capabilities = profile.Capabilities,
                Mcp = profile.Mcp,
                Tools = profile.Tools,
                Permissions = profile.Permissions,
            },
            Files = fileDtos,
            FileContents = contents,
        };

        var snapshotJson = JsonSerializer.Serialize(dto, Options);
        var configHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotJson))).ToLowerInvariant();
        return new ProfileSnapshot(profile.Name, $"config/agents/{profile.Name}", snapshotJson, configHash);
    }

    private sealed class SnapshotDto
    {
        [JsonPropertyName("profile")] public string Profile { get; set; } = null!;
        [JsonPropertyName("source_directory")] public string SourceDirectory { get; set; } = null!;
        [JsonPropertyName("source_commit_sha")] public string? SourceCommitSha { get; set; }
        [JsonPropertyName("runtime_type")] public string RuntimeType { get; set; } = null!;
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("profile_yaml")] public ProfileYamlDto ProfileYaml { get; set; } = null!;
        [JsonPropertyName("files")] public List<FileEntryDto> Files { get; set; } = null!;
        [JsonPropertyName("file_contents")] public SortedDictionary<string, string> FileContents { get; set; } = null!;
    }

    private sealed record FileEntryDto([property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("sha256")] string Sha256);

    /// <summary>snake_case mirror of profile.yaml (same keys the ConfigParser DTOs read).</summary>
    private sealed class ProfileYamlDto
    {
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("runtime_type")] public string? RuntimeType { get; set; }
        [JsonPropertyName("max_context_tokens")] public long MaxContextTokens { get; set; }
        [JsonPropertyName("startup_timeout")] public TimeSpan? StartupTimeout { get; set; }
        [JsonPropertyName("idle_timeout")] public TimeSpan? IdleTimeout { get; set; }
        [JsonPropertyName("max_run_duration")] public TimeSpan? MaxRunDuration { get; set; }
        [JsonPropertyName("cpu")] public decimal? Cpu { get; set; }
        [JsonPropertyName("memory")] public string? Memory { get; set; }
        [JsonPropertyName("max_automatic_retries")] public int? MaxAutomaticRetries { get; set; }
        [JsonPropertyName("initial_backoff")] public TimeSpan? InitialBackoff { get; set; }
        [JsonPropertyName("bridge_version")] public string? BridgeVersion { get; set; }
        [JsonPropertyName("heartbeat_interval")] public TimeSpan? BridgeHeartbeatInterval { get; set; }
        [JsonPropertyName("event_cadence")] public TimeSpan? BridgeEventCadence { get; set; }
        [JsonPropertyName("behavior")] public string? BridgeBehavior { get; set; }
        [JsonPropertyName("system_prompt")] public string? SystemPrompt { get; set; }
        [JsonPropertyName("skills")] public List<string>? Skills { get; set; }
        [JsonPropertyName("secrets")] public List<string>? Secrets { get; set; }
        [JsonPropertyName("capabilities")] public List<string>? Capabilities { get; set; }
        [JsonPropertyName("mcp")] public Dictionary<string, object?>? Mcp { get; set; }
        [JsonPropertyName("tools")] public Dictionary<string, object?>? Tools { get; set; }
        [JsonPropertyName("permissions")] public Dictionary<string, object?>? Permissions { get; set; }
    }
}
