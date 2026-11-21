using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PiAgent.Infrastructure.Persistence;

namespace PiAgent.Orchestrator.Config;

/// <summary>Everything the DB sync needs for one validated configuration run, prepared by the loader.</summary>
public sealed record ConfigSynchronization(
    ParsedConfig Config,
    /// <summary>Key: normalized lowercase `owner/name`; values from GET /repos/{owner}/{repo} onboarding lookups.</summary>
    IReadOnlyDictionary<string, GitHubRepoInfo> Repositories,
    IReadOnlyList<ProfileSnapshot> Snapshots,
    string? SourceCommitSha);

public sealed record SyncResult(
    int RepositoriesCreated,
    int RepositoriesUpdated,
    int RepositoriesEnabled,
    int RepositoriesDisabled,
    int CursorsCreated,
    int ProfilesUpserted,
    int ProfilesDisabled,
    int VersionsAdded,
    int RoutingRulesUpserted,
    int RoutingRulesDeleted);

/// <summary>
/// Persists the parsed configuration plane in ONE transaction (02 checklist: no partial DB rows) —
/// repositories + decision-53 sync cursors, agent profiles + `draft` versions pinned via config_hash
/// (decision 81: unchanged hash ⇒ no new version, never a build dispatch), and the seeded routing rules
/// (02 §12a). YAML is the desired state: rows absent from YAML get paused (`is_enabled=false`),
/// routing rules absent from YAML are deleted (configuration, not orchestration history). The Config
/// Loader never builds or dispatches images — only tickets 07/10 transition version states.
/// </summary>
public sealed class ConfigSyncService(AppDbContext db, ILogger<ConfigSyncService>? logger = null)
{
    /// <summary>Decision 36 phase-1 command set; repositories.yaml may narrow it via permitted_commands.</summary>
    private static readonly string[] AllV1Commands = ["plan", "code", "review", "test-cases", "status", "stop", "help", "retry"];
    private static readonly string[] AllV1WorkItemTypes = ["issue", "pull_request"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public async Task<SyncResult> SyncAsync(ConfigSynchronization sync, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var (repositoriesCreated, repositoriesUpdated, repositoriesEnabled, repositoriesDisabled, cursorsCreated, repositoryRows) =
                await SyncRepositoriesAsync(sync, cancellationToken);
            var (profilesUpserted, profilesDisabled, versionsAdded, latestVersions, profileRows) =
                await SyncProfilesAsync(sync, cancellationToken);
            WarnOnTightenedSystemLimits(sync, latestVersions);
            var (rulesUpserted, rulesDeleted) = await SyncRoutingAsync(sync, latestVersions, repositoryRows, profileRows, cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new SyncResult(
                repositoriesCreated, repositoriesUpdated, repositoriesEnabled, repositoriesDisabled,
                cursorsCreated, profilesUpserted, profilesDisabled, versionsAdded, rulesUpserted, rulesDeleted);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<(int Created, int Updated, int Enabled, int Disabled, int Cursors, Dictionary<string, Repository> Rows)> SyncRepositoriesAsync(
        ConfigSynchronization sync, CancellationToken cancellationToken)
    {
        var existing = await db.Repositories.ToListAsync(cancellationToken);
        var rowsByName = existing.ToDictionary(r => r.FullName);
        var existingByName = new Dictionary<string, Repository>(rowsByName);
        var desiredNames = new HashSet<string>();
        int created = 0, updated = 0, enabledRows = 0, cursors = 0;

        foreach (var entry in sync.Config.Repositories)
        {
            var fullName = $"{entry.Owner}/{entry.Name}".ToLowerInvariant();
            desiredNames.Add(fullName);
            var info = sync.Repositories[fullName];
            var configuration = BuildConfiguration(existingByName.GetValueOrDefault(fullName), entry);

            if (!existingByName.TryGetValue(fullName, out var repository))
            {
                var now = DateTime.UtcNow;
                repository = new Repository
                {
                    Id = Guid.NewGuid(),
                    GithubRepositoryId = info.Id,
                    Owner = entry.Owner,
                    Name = entry.Name,
                    FullName = fullName,
                    DefaultBranch = info.DefaultBranch,
                    IsEnabled = entry.Enabled,
                    Configuration = configuration,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.Repositories.Add(repository);
                rowsByName[repository.FullName] = repository;
                foreach (var stream in SyncCursorStreams.All)
                {
                    db.SyncCursors.Add(new SyncCursor
                    {
                        Id = Guid.NewGuid(),
                        RepositoryId = repository.Id,
                        Stream = stream,
                        CursorValue = null,
                        UpdatedAt = now,
                    });
                    cursors++;
                }

                created++;
                continue;
            }

            var changed = false;
            if (repository.GithubRepositoryId != info.Id || repository.DefaultBranch != info.DefaultBranch
                || !string.Equals(repository.Configuration, configuration, StringComparison.Ordinal))
            {
                repository.GithubRepositoryId = info.Id;
                repository.DefaultBranch = info.DefaultBranch;
                repository.Configuration = configuration;
                changed = true;
            }

            if (repository.IsEnabled != entry.Enabled)
            {
                repository.IsEnabled = entry.Enabled;
                if (entry.Enabled)
                    enabledRows++;
                changed = true;
            }

            if (changed)
            {
                repository.UpdatedAt = DateTime.UtcNow;
                updated++;
            }
        }

        // rows absent from YAML are paused, never deleted (decision 75 pause semantics)
        var disabled = 0;
        foreach (var repository in existing.Where(r => !desiredNames.Contains(r.FullName) && r.IsEnabled))
        {
            repository.IsEnabled = false;
            repository.UpdatedAt = DateTime.UtcNow;
            disabled++;
        }

        return (created, updated, enabledRows, disabled, cursors, rowsByName);
    }

    private static string BuildConfiguration(Repository? existing, RepositoryEntry entry)
    {
        // decision 49/§21: absent allowlist = enabled, seeded with the owner when the repo is new;
        // an explicitly emptied allowlist ([] in YAML) disables list filtering, keeping the write baseline.
        // absent → enabled; explicit [] → filtering disabled; explicit list → enabled with that list (decision 49)
        var enableAllowlist = entry.AllowedLogins is null || entry.AllowedLogins.Count > 0;
        var logins = entry.AllowedLogins switch
        {
            null => enableAllowlist
                ? (existing is not null ? StoredLogins(existing) : [entry.Owner.ToLowerInvariant()])
                : [],
            { } explicitLogins => explicitLogins.Select(l => l.ToLowerInvariant()).ToList(),
        };

        return JsonSerializer.Serialize(new RepositoryConfigurationDto
        {
            Allowlist = new AllowlistConfigurationDto { Enabled = enableAllowlist, Logins = logins },
            PermittedCommands = entry.PermittedCommands ?? [.. AllV1Commands],
            PermittedWorkItemTypes = entry.PermittedWorkItemTypes ?? [.. AllV1WorkItemTypes],
            Authorization = new AuthorizationConfigurationDto { MinimumPermission = entry.MinimumPermission ?? "write" },
        }, JsonOptions);
    }

    private static bool AllowlistEnabled(Repository? existing)
    {
        if (existing is null)
            return true;
        try
        {
            using var json = JsonDocument.Parse(existing.Configuration);
            return json.RootElement.GetProperty("allowlist").GetProperty("enabled").GetBoolean();
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static List<string> StoredLogins(Repository existing)
    {
        try
        {
            using var json = JsonDocument.Parse(existing.Configuration);
            return json.RootElement.GetProperty("allowlist").GetProperty("logins")
                .EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
        }
        catch (Exception)
        {
            return [existing.Owner.ToLowerInvariant()];
        }
    }

    private sealed class RepositoryConfigurationDto
    {
        [JsonPropertyName("allowlist")] public AllowlistConfigurationDto Allowlist { get; set; } = null!;
        [JsonPropertyName("permitted_commands")] public IReadOnlyList<string> PermittedCommands { get; set; } = [];
        [JsonPropertyName("permitted_work_item_types")] public IReadOnlyList<string> PermittedWorkItemTypes { get; set; } = [];
        [JsonPropertyName("authorization")] public AuthorizationConfigurationDto Authorization { get; set; } = null!;
    }

    private sealed class AllowlistConfigurationDto
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("logins")] public IReadOnlyList<string> Logins { get; set; } = [];
    }

    private sealed class AuthorizationConfigurationDto
    {
        [JsonPropertyName("minimum_permission")] public string MinimumPermission { get; set; } = "write";
    }

    private async Task<(int Upserted, int Disabled, int VersionsAdded, Dictionary<string, AgentProfileVersion> Latest, Dictionary<string, AgentProfile> ProfileRows)> SyncProfilesAsync(
        ConfigSynchronization sync, CancellationToken cancellationToken)
    {
        var existing = await db.AgentProfiles.ToListAsync(cancellationToken);
        var rowsByName = existing.ToDictionary(p => p.Name);
        var existingByName = rowsByName;
        var latestByProfileId = (await db.AgentProfileVersions.ToListAsync(cancellationToken))
            .GroupBy(v => v.AgentProfileId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.Version).First());
        var snapshotsByName = sync.Snapshots.ToDictionary(s => s.ProfileName);
        int upserted = 0, disabled = 0, versionsAdded = 0;
        var latest = new Dictionary<string, AgentProfileVersion>();

        foreach (var profile in sync.Config.Profiles)
        {
            var snapshot = snapshotsByName[profile.Name];
            if (!existingByName.TryGetValue(profile.Name, out var profileRow))
            {
                var now = DateTime.UtcNow;
                profileRow = new AgentProfile
                {
                    Id = Guid.NewGuid(),
                    Name = profile.Name,
                    Description = profile.Description,
                    IsEnabled = profile.Enabled,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.AgentProfiles.Add(profileRow);
                rowsByName[profileRow.Name] = profileRow;
                upserted++;
            }

            // decision 81: a new version only when the latest version's config_hash differs
            if (latestByProfileId.TryGetValue(profileRow.Id, out var current) && current.ConfigHash == snapshot.ConfigHash)
            {
                latest[profile.Name] = current;
                continue;
            }

            var version = new AgentProfileVersion
            {
                Id = Guid.NewGuid(),
                AgentProfileId = profileRow.Id,
                Version = latestByProfileId.TryGetValue(profileRow.Id, out var previous) ? previous.Version + 1 : 1,
                SourceCommitSha = sync.SourceCommitSha,
                SourceDirectory = snapshot.SourceDirectory,
                ConfigSnapshot = snapshot.SnapshotJson,
                ConfigHash = snapshot.ConfigHash,
                RuntimeType = profile.RuntimeType,
                SystemPrompt = profile.SystemPrompt,
                SkillsConfiguration = SerializeOrNull(() => new { skills = profile.Skills }),
                McpConfiguration = profile.Mcp is null ? null : JsonSerializer.Serialize(profile.Mcp, JsonOptions),
                ToolsConfiguration = profile.Tools is null ? null : JsonSerializer.Serialize(profile.Tools, JsonOptions),
                EnvironmentConfiguration = SerializeOrNull(() => new { secret_references = profile.Secrets }),
                ResourceConfiguration = SerializeOrNull(
                    profile.Cpu is not null || profile.Memory is not null,
                    () => new ResourceConfigurationDto { Cpu = profile.Cpu, Memory = profile.Memory }),
                Capabilities = SerializeOrNull(() => new { capabilities = profile.Capabilities }),
                RetryPolicy = SerializeOrNull(
                    profile.MaxAutomaticRetries is not null || profile.InitialBackoff is not null,
                    () => new RetryPolicyDto { MaxAutomaticRetries = profile.MaxAutomaticRetries, InitialBackoff = profile.InitialBackoff }),
                PermissionPolicy = profile.Permissions is null ? null : JsonSerializer.Serialize(profile.Permissions, JsonOptions),
                BridgeConfiguration = SerializeOrNull(
                    profile.BridgeVersion is not null || profile.BridgeHeartbeatInterval is not null || profile.BridgeEventCadence is not null || profile.BridgeBehavior is not null,
                    () => new BridgeConfigurationDto
                    {
                        Version = profile.BridgeVersion,
                        HeartbeatInterval = profile.BridgeHeartbeatInterval,
                        EventCadence = profile.BridgeEventCadence,
                        Behavior = profile.BridgeBehavior,
                    }),
                StartupTimeout = profile.StartupTimeout,
                IdleTimeout = profile.IdleTimeout,
                MaxRunDuration = profile.MaxRunDuration,
                MaxContextTokens = profile.MaxContextTokens,
                State = ProfileVersionStates.Draft,
                CreatedAt = DateTime.UtcNow,
            };
            db.AgentProfileVersions.Add(version);
            latest[profile.Name] = version;
            versionsAdded++;
        }

        // profile directories absent from YAML: paused, never deleted (symmetric with repositories)
        foreach (var profileRow in existing.Where(p => !snapshotsByName.ContainsKey(p.Name) && p.IsEnabled))
        {
            profileRow.IsEnabled = false;
            profileRow.UpdatedAt = DateTime.UtcNow;
            disabled++;
        }

        return (upserted, disabled, versionsAdded, latest, rowsByName);
    }

    /// <summary>The list-valued helpers fire only when the parsed profile actually carried the section; absent stays NULL like YAML says.</summary>
    private static string? SerializeOrNull(Func<object?> build)
    {
        if (build() is not { } value)
            return null;
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static string? SerializeOrNull(bool hasValue, Func<object> build)
    {
        if (!hasValue)
            return null;
        return JsonSerializer.Serialize(build(), JsonOptions);
    }

    private void WarnOnTightenedSystemLimits(ConfigSynchronization sync, Dictionary<string, AgentProfileVersion> latest)
    {
        if (logger is null)
            return;
        var system = sync.Config.System;
        foreach (var (profileName, version) in latest)
        {
            if (version.StartupTimeout is { } startup && startup > system.StartupTimeout)
                logger.LogWarning("profile {profile} version {version} startup_timeout {limit} exceeds the current system constraint {system} — new versions are validated, this one was created under looser limits",
                    profileName, version.Version, startup, system.StartupTimeout);
            if (version.IdleTimeout is { } idle && idle > system.IdleTimeout)
                logger.LogWarning("profile {profile} version {version} idle_timeout {limit} exceeds the current system constraint {system}",
                    profileName, version.Version, idle, system.IdleTimeout);
            if (version.MaxRunDuration is { } maxRun && maxRun > system.MaxRunDuration)
                logger.LogWarning("profile {profile} version {version} max_run_duration {limit} exceeds the current system constraint {system}",
                    profileName, version.Version, maxRun, system.MaxRunDuration);
        }
    }

    private sealed class ResourceConfigurationDto
    {
        [JsonPropertyName("cpu")] public decimal? Cpu { get; set; }
        [JsonPropertyName("memory")] public string? Memory { get; set; }
    }

    private sealed class RetryPolicyDto
    {
        [JsonPropertyName("max_automatic_retries")] public int? MaxAutomaticRetries { get; set; }
        [JsonPropertyName("initial_backoff")] public TimeSpan? InitialBackoff { get; set; }
    }

    private sealed class BridgeConfigurationDto
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("heartbeat_interval")] public TimeSpan? HeartbeatInterval { get; set; }
        [JsonPropertyName("event_cadence")] public TimeSpan? EventCadence { get; set; }
        [JsonPropertyName("behavior")] public string? Behavior { get; set; }
    }

    private async Task<(int Upserted, int Deleted)> SyncRoutingAsync(
        ConfigSynchronization sync,
        Dictionary<string, AgentProfileVersion> latestVersions,
        Dictionary<string, Repository> repositoryRows,
        Dictionary<string, AgentProfile> profileRows,
        CancellationToken cancellationToken)
    {
        var repositoryNameById = repositoryRows.Values.ToDictionary(r => r.Id, r => r.FullName);
        var existingRules = await db.AgentProfileRoutingRules.ToListAsync(cancellationToken);
        int upserted = 0;

        foreach (var rule in sync.Config.RoutingRules)
        {
            // Validator rejects rules referencing absent profiles or profiles without versions before sync
            // runs; direct sync calls leave such rules untouched rather than throw mid-transaction.
            if (!profileRows.TryGetValue(rule.Profile, out _) || !latestVersions.TryGetValue(rule.Profile, out var latestVersion))
                continue;

            Guid? repositoryId = null;
            if (rule.Repository is { } repository)
            {
                var fullName = repository.ToLowerInvariant();
                if (!repositoryRows.TryGetValue(fullName, out var repositoryRow))
                    throw new ConfigException($"routing.yaml: rule task_type '{rule.TaskType}' references unknown repository '{repository}'");
                repositoryId = repositoryRow.Id;
            }

            var scope = (RepositoryId: repositoryId, TaskType: rule.TaskType, Priority: rule.Priority);
            var existing = existingRules.FirstOrDefault(r =>
                r.RepositoryId == scope.RepositoryId && r.TaskType == scope.TaskType && r.Priority == scope.Priority);
            if (existing is null)
            {
                db.AgentProfileRoutingRules.Add(new AgentProfileRoutingRule
                {
                    Id = Guid.NewGuid(),
                    RepositoryId = repositoryId,
                    TaskType = rule.TaskType,
                    AgentProfileVersionId = latestVersion.Id,
                    Priority = rule.Priority,
                    IsEnabled = rule.Enabled,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
            else
            {
                existing.AgentProfileVersionId = latestVersion.Id;
                existing.IsEnabled = rule.Enabled;
                existing.UpdatedAt = DateTime.UtcNow;
            }

            upserted++;
        }

        // desired-state reconcile: rules no longer in YAML are removed (configuration, not orchestration history)
        int deleted = 0;
        foreach (var rule in existingRules)
        {
            var ruleRepoName = rule.RepositoryId is { } id ? repositoryNameById.GetValueOrDefault(id) : null;
            var stillDesired = sync.Config.RoutingRules.Any(desired =>
                desired.Repository?.ToLowerInvariant() == ruleRepoName
                && desired.TaskType == rule.TaskType
                && desired.Priority == rule.Priority);
            if (stillDesired)
                continue;

            db.AgentProfileRoutingRules.Remove(rule);
            deleted++;
        }

        return (upserted, deleted);
    }
}
