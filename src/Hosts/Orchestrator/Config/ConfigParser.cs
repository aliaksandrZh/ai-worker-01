using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Config;
using YamlDotNet.Serialization;

namespace PiAgent.Orchestrator.Config;

/// <summary>
/// Reads the configuration plane from `config/`: system.yaml, repositories.yaml, routing.yaml,
/// agents/&lt;name&gt;/profile.yaml. Strict schema — unknown keys, missing mandatory keys, YAML syntax
/// errors and illegal values all fail with ConfigException naming the file.
/// Validation of numeric constraints lives in ConfigValidator; this type only parses.
/// </summary>
public static class ConfigParser
{
    private static readonly string[] LegalTaskTypes = ["plan", "implement", "review", "test_design"];

    public static ParsedConfig Parse(string configRoot)
    {
        if (!Directory.Exists(configRoot))
            throw new ConfigException($"configuration directory '{configRoot}' does not exist");

        var system = ParseSystem(Path.Combine(configRoot, "system.yaml"));
        var repositories = ParseRepositories(Path.Combine(configRoot, "repositories.yaml"));
        var routing = ParseRouting(Path.Combine(configRoot, "routing.yaml"));
        var profiles = ParseProfiles(Path.Combine(configRoot, "agents"));
        var (boardTriggers, statusConfigs) = ParseProjects(Path.Combine(configRoot, "projects.yaml"));

        return new ParsedConfig
        {
            System = system,
            Repositories = repositories,
            RoutingRules = routing,
            Profiles = profiles,
            ProjectBoardTriggers = boardTriggers,
            ProjectStatusConfigs = statusConfigs,
        };
    }

    // Duplicate YAML keys silently resolve last-wins by default — a config-integrity hazard the
    // spec's "diagnosable error" bar does not allow, so duplicates throw and surface as ConfigException.
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithDuplicateKeyChecking()
        .Build();

    /// <summary>Strict-YAML load; YamlDotNet fails on unmatched properties by default. Errors name the file.</summary>
    private static T Load<T>(string path, string relativeName) where T : class
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException($"{relativeName}: cannot read configuration file ({exception.Message})");
        }

        try
        {
            return Yaml.Deserialize<T>(text) ?? throw new ConfigException($"{relativeName}: file is empty, expected a YAML mapping");
        }
        catch (YamlDotNet.Core.YamlException exception)
        {
            throw new ConfigException($"{relativeName}: {exception.Message}");
        }
    }

    private sealed class SystemDto
    {
        [YamlMember(Alias = "github")] public GitHubDto? GitHub { get; set; }
        [YamlMember(Alias = "poll_interval")] public string? PollInterval { get; set; }
        [YamlMember(Alias = "poll_overlap")] public string? PollOverlap { get; set; }
        [YamlMember(Alias = "per_page")] public long? PerPage { get; set; }
        [YamlMember(Alias = "max_concurrent_runs")] public int? MaxConcurrentRuns { get; set; }
        [YamlMember(Alias = "startup_timeout")] public string? StartupTimeout { get; set; }
        [YamlMember(Alias = "idle_timeout")] public string? IdleTimeout { get; set; }
        [YamlMember(Alias = "max_run_duration")] public string? MaxRunDuration { get; set; }
        [YamlMember(Alias = "heartbeat_interval")] public string? HeartbeatInterval { get; set; }
        [YamlMember(Alias = "max_log_size")] public string? MaxLogSize { get; set; }
        [YamlMember(Alias = "max_artifact_size")] public string? MaxArtifactSize { get; set; }
        [YamlMember(Alias = "max_task_artifacts_size")] public string? MaxTaskArtifactsSize { get; set; }
        [YamlMember(Alias = "max_automatic_retries")] public int? MaxAutomaticRetries { get; set; }
        [YamlMember(Alias = "outbox_retry_window")] public string? OutboxRetryWindow { get; set; }
        [YamlMember(Alias = "orphan_grace_period")] public string? OrphanGracePeriod { get; set; }
        [YamlMember(Alias = "orphan_quarantine_period")] public string? OrphanQuarantinePeriod { get; set; }
    }

    private sealed class GitHubDto
    {
        [YamlMember(Alias = "bot_login")] public string? BotLogin { get; set; }
    }

    private static SystemConfig ParseSystem(string path)
    {
        const string name = "system.yaml";
        var dto = Load<SystemDto>(path, name);

        var github = dto.GitHub;
        if (string.IsNullOrWhiteSpace(github?.BotLogin))
            throw new ConfigException($"{name}: github.bot_login is required (the bot account mentioned in commands)");

        return new SystemConfig
        {
            BotLogin = github.BotLogin,
            PollInterval = ParseKnobDuration(name, "poll_interval", dto.PollInterval, TimeSpan.FromSeconds(60)),
            PollOverlap = ParseKnobDuration(name, "poll_overlap", dto.PollOverlap, TimeSpan.FromMinutes(5)),
            PerPage = dto.PerPage ?? 100,
            MaxConcurrentRuns = dto.MaxConcurrentRuns ?? 10,
            StartupTimeout = ParseKnobDuration(name, "startup_timeout", dto.StartupTimeout, TimeSpan.FromMinutes(10)),
            IdleTimeout = ParseKnobDuration(name, "idle_timeout", dto.IdleTimeout, TimeSpan.FromMinutes(60)),
            MaxRunDuration = ParseKnobDuration(name, "max_run_duration", dto.MaxRunDuration, TimeSpan.FromHours(4)),
            HeartbeatInterval = ParseKnobDuration(name, "heartbeat_interval", dto.HeartbeatInterval, TimeSpan.FromSeconds(30)),
            MaxLogSize = ParseKnobSize(name, "max_log_size", dto.MaxLogSize, 50 * 1024 * 1024),
            MaxArtifactSize = ParseKnobSize(name, "max_artifact_size", dto.MaxArtifactSize, 256 * 1024),
            MaxTaskArtifactsSize = ParseKnobSize(name, "max_task_artifacts_size", dto.MaxTaskArtifactsSize, 512 * 1024),
            MaxAutomaticRetries = dto.MaxAutomaticRetries ?? 1,
            OutboxRetryWindow = ParseKnobDuration(name, "outbox_retry_window", dto.OutboxRetryWindow, TimeSpan.FromHours(24)),
            OrphanGracePeriod = ParseKnobDuration(name, "orphan_grace_period", dto.OrphanGracePeriod, TimeSpan.FromHours(24)),
            OrphanQuarantinePeriod = ParseKnobDuration(name, "orphan_quarantine_period", dto.OrphanQuarantinePeriod, TimeSpan.FromHours(24)),
        };
    }

    private static TimeSpan ParseKnobDuration(string file, string key, string? value, TimeSpan fallback)
    {
        if (value is null)
            return fallback;

        try
        {
            return SystemYaml.ParseDurationValue(value);
        }
        catch (ArgumentException exception)
        {
            throw new ConfigException($"{file}: {key}: {exception.Message}");
        }
    }

    private static long ParseKnobSize(string file, string key, string? value, long fallback)
    {
        if (value is null)
            return fallback;
        try
        {
            return SizeValue.Parse(value);
        }
        catch (ArgumentException exception)
        {
            throw new ConfigException($"{file}: {key}: {exception.Message}");
        }
    }

    private sealed class RepositoriesDto
    {
        [YamlMember(Alias = "repositories")] public List<RepositoryDto>? Items { get; set; }
    }

    private sealed class RepositoryDto
    {
        [YamlMember(Alias = "owner")] public string? Owner { get; set; }
        [YamlMember(Alias = "name")] public string? Name { get; set; }
        [YamlMember(Alias = "enabled")] public bool? Enabled { get; set; }
        [YamlMember(Alias = "allowlist")] public List<string>? AllowedLogins { get; set; }
        [YamlMember(Alias = "permitted_commands")] public List<string>? PermittedCommands { get; set; }
        [YamlMember(Alias = "permitted_work_item_types")] public List<string>? PermittedWorkItemTypes { get; set; }
        [YamlMember(Alias = "authorization_policy")] public AuthorizationPolicyDto? AuthorizationPolicy { get; set; }
    }

    private sealed class AuthorizationPolicyDto
    {
        [YamlMember(Alias = "minimum_permission")] public string? MinimumPermission { get; set; }
    }

    private static List<RepositoryEntry> ParseRepositories(string path)
    {
        const string name = "repositories.yaml";
        var dto = Load<RepositoriesDto>(path, name);

        var entries = new List<RepositoryEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in dto.Items ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Owner))
                throw new ConfigException($"{name}: repository entry is missing `owner`");
            if (string.IsNullOrWhiteSpace(item.Name))
                throw new ConfigException($"{name}: repository entry is missing `name`");
            var fullName = $"{item.Owner}/{item.Name}".ToLowerInvariant();
            if (!seen.Add(fullName))
                throw new ConfigException($"{name}: duplicate repository full_name '{fullName}' (normalized full_name must be unique)");

            entries.Add(new RepositoryEntry
            {
                Owner = item.Owner,
                Name = item.Name,
                Enabled = item.Enabled ?? true,
                AllowedLogins = item.AllowedLogins,
                PermittedCommands = item.PermittedCommands,
                PermittedWorkItemTypes = item.PermittedWorkItemTypes,
                MinimumPermission = item.AuthorizationPolicy?.MinimumPermission,
            });
        }

        return entries;
    }

    private sealed class RoutingDto
    {
        [YamlMember(Alias = "rules")] public List<RoutingRuleDto>? Rules { get; set; }
    }

    private sealed class RoutingRuleDto
    {
        [YamlMember(Alias = "task_type")] public string? TaskType { get; set; }
        [YamlMember(Alias = "profile")] public string? Profile { get; set; }
        [YamlMember(Alias = "repository")] public string? Repository { get; set; }
        [YamlMember(Alias = "priority")] public int? Priority { get; set; }
        [YamlMember(Alias = "enabled")] public bool? Enabled { get; set; }
    }

    private static List<RoutingRuleEntry> ParseRouting(string path)
    {
        const string name = "routing.yaml";
        var dto = Load<RoutingDto>(path, name);

        var rules = new List<RoutingRuleEntry>();
        foreach (var item in dto.Rules ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.TaskType) || !LegalTaskTypes.Contains(item.TaskType))
                throw new ConfigException(
                    $"{name}: rule task_type '{item.TaskType}' is not one of {string.Join("|", LegalTaskTypes)}");
            if (string.IsNullOrWhiteSpace(item.Profile))
                throw new ConfigException($"{name}: rule for task_type '{item.TaskType}' is missing `profile`");

            rules.Add(new RoutingRuleEntry
            {
                TaskType = item.TaskType,
                Profile = item.Profile,
                Repository = item.Repository,
                Priority = item.Priority ?? 1,
                Enabled = item.Enabled ?? true,
            });
        }

        return rules;
    }

    // Board sync is the Poller's concern; the Orchestrator reads the same projects.yaml only for
    // its triggers. Absent file = no boards, no triggers — valid (the Poller is the one that
    // requires the file when boards are configured).
    private sealed class ProjectsDto
    {
        [YamlMember(Alias = "projects")] public List<ProjectDto>? Projects { get; set; }
    }

    private sealed class ProjectDto
    {
        [YamlMember(Alias = "owner")] public string? Owner { get; set; }
        [YamlMember(Alias = "number")] public int? Number { get; set; }
        [YamlMember(Alias = "auto_plan")] public AutoPlanDto? AutoPlan { get; set; }
        [YamlMember(Alias = "auto_code")] public AutoPlanDto? AutoCode { get; set; }
        [YamlMember(Alias = "ai_statuses")] public AiStatusesDto? AiStatuses { get; set; }
    }

    private sealed class AutoPlanDto
    {
        [YamlMember(Alias = "status_field")] public string? StatusField { get; set; }
        [YamlMember(Alias = "from")] public string? From { get; set; }
        [YamlMember(Alias = "to")] public string? To { get; set; }
    }

    private sealed class AiStatusesDto
    {
        [YamlMember(Alias = "status_field")] public string? StatusField { get; set; }
        [YamlMember(Alias = "processing")] public string? Processing { get; set; }
        [YamlMember(Alias = "review")] public string? Review { get; set; }
        [YamlMember(Alias = "code_lock_statuses")] public List<string>? CodeLockStatuses { get; set; }
    }

    private static (List<ProjectBoardTrigger> Triggers, List<ProjectStatusConfig> StatusConfigs) ParseProjects(string path)
    {
        const string name = "projects.yaml";
        if (!File.Exists(path))
            return ([], []);

        var dto = Load<ProjectsDto>(path, name);
        var triggers = new List<ProjectBoardTrigger>();
        var statusConfigs = new List<ProjectStatusConfig>();
        foreach (var item in dto.Projects ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Owner))
                throw new ConfigException($"{name}: project entry is missing `owner`");
            if (item.Number is not { } number || number <= 0)
                throw new ConfigException($"{name}: project entry '{item.Owner}' must have a positive `number`");

            foreach (var (triggerDto, key, taskType) in new[]
                     {
                     (item.AutoPlan, "auto_plan", OrchestrationTaskTypes.Plan),
                     (item.AutoCode, "auto_code", OrchestrationTaskTypes.Implement),
                     })
            {
                if (triggerDto is not { } block)
                    continue;
                if (string.IsNullOrWhiteSpace(block.From) || string.IsNullOrWhiteSpace(block.To))
                    throw new ConfigException(
                        $"{name}: {key} for project '{item.Owner}/{number}' requires non-empty `from` and `to`");
                if (string.Equals(block.From, block.To, StringComparison.Ordinal))
                    throw new ConfigException(
                        $"{name}: {key} for project '{item.Owner}/{number}': `from` and `to` must differ");
                triggers.Add(new ProjectBoardTrigger(item.Owner.Trim(), number,
                    string.IsNullOrWhiteSpace(block.StatusField) ? "Status" : block.StatusField.Trim(),
                    block.From.Trim(), block.To.Trim(), taskType));
            }

            if (item.AiStatuses is { } statuses)
            {
                if (string.IsNullOrWhiteSpace(statuses.Processing) && string.IsNullOrWhiteSpace(statuses.Review))
                    throw new ConfigException(
                        $"{name}: ai_statuses for project '{item.Owner}/{number}' requires `processing` and/or `review`");
                statusConfigs.Add(new ProjectStatusConfig(item.Owner.Trim(), number,
                    string.IsNullOrWhiteSpace(statuses.StatusField) ? "Status" : statuses.StatusField.Trim(),
                    string.IsNullOrWhiteSpace(statuses.Processing) ? null : statuses.Processing.Trim(),
                    string.IsNullOrWhiteSpace(statuses.Review) ? null : statuses.Review.Trim(),
                    (statuses.CodeLockStatuses ?? [])
                        .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList()));
            }
        }
        return (triggers, statusConfigs);
    }

    private sealed class ProfileDto
    {
        [YamlMember(Alias = "description")] public string? Description { get; set; }
        [YamlMember(Alias = "enabled")] public bool? Enabled { get; set; }
        [YamlMember(Alias = "runtime_type")] public string? RuntimeType { get; set; }
        [YamlMember(Alias = "max_context_tokens")] public long? MaxContextTokens { get; set; }
        [YamlMember(Alias = "execution_limits")] public ExecutionLimitsDto? ExecutionLimits { get; set; }
        [YamlMember(Alias = "resources")] public ResourcesDto? Resources { get; set; }
        [YamlMember(Alias = "retry_policy")] public RetryPolicyDto? RetryPolicy { get; set; }
        [YamlMember(Alias = "bridge")] public BridgeDto? Bridge { get; set; }
        [YamlMember(Alias = "system_prompt")] public string? SystemPrompt { get; set; }
        [YamlMember(Alias = "secrets")] public List<string>? Secrets { get; set; }
        [YamlMember(Alias = "capabilities")] public List<string>? Capabilities { get; set; }
        [YamlMember(Alias = "skills")] public List<string>? Skills { get; set; }
        [YamlMember(Alias = "mcp")] public Dictionary<string, object?>? Mcp { get; set; }
        [YamlMember(Alias = "tools")] public Dictionary<string, object?>? Tools { get; set; }
        [YamlMember(Alias = "permissions")] public Dictionary<string, object?>? Permissions { get; set; }
    }

    private sealed class ExecutionLimitsDto
    {
        [YamlMember(Alias = "startup_timeout")] public string? StartupTimeout { get; set; }
        [YamlMember(Alias = "idle_timeout")] public string? IdleTimeout { get; set; }
        [YamlMember(Alias = "max_run_duration")] public string? MaxRunDuration { get; set; }
    }

    private sealed class ResourcesDto
    {
        [YamlMember(Alias = "cpu")] public decimal? Cpu { get; set; }
        [YamlMember(Alias = "memory")] public string? Memory { get; set; }
    }

    private sealed class RetryPolicyDto
    {
        [YamlMember(Alias = "max_automatic_retries")] public int? MaxAutomaticRetries { get; set; }
        [YamlMember(Alias = "initial_backoff")] public string? InitialBackoff { get; set; }
    }

    private sealed class BridgeDto
    {
        [YamlMember(Alias = "version")] public string? Version { get; set; }
        [YamlMember(Alias = "heartbeat_interval")] public string? HeartbeatInterval { get; set; }
        [YamlMember(Alias = "event_cadence")] public string? EventCadence { get; set; }
        [YamlMember(Alias = "behavior")] public string? Behavior { get; set; }
    }

    private static List<ProfileConfig> ParseProfiles(string agentsPath)
    {
        if (!Directory.Exists(agentsPath))
            throw new ConfigException($"agents: profiles directory 'agents' is missing under the configuration root");

        var profiles = new List<ProfileConfig>();
        foreach (var profileDir in Directory.EnumerateDirectories(agentsPath).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(profileDir);
            if (name.StartsWith("_", StringComparison.Ordinal))
                continue; // convention: `_`-prefixed agents/ subdirectories are shared runtime assets
                          // (e.g. `_runtime/bridge`), not agent profiles — everything else must be one
                          // (a directory without profile.yaml still aborts startup, so profile-name
                          // typos fail closed as before)
            var relativeName = $"agents/{name}/profile.yaml";
            var profileYamlPath = Path.Combine(profileDir, "profile.yaml");
            if (!File.Exists(profileYamlPath))
                throw new ConfigException($"agents: profile directory '{name}' has no profile.yaml");

            var dto = Load<ProfileDto>(profileYamlPath, relativeName);
            if (dto.MaxContextTokens is null || dto.MaxContextTokens <= 0)
                throw new ConfigException($"{relativeName}: max_context_tokens is required and must be positive");

            var runtimeType = dto.RuntimeType ?? "pi";
            if (runtimeType != "pi")
                throw new ConfigException($"{relativeName}: runtime_type '{runtimeType}' is not supported (only `pi` is supported)");

            profiles.Add(new ProfileConfig
            {
                Name = name,
                Description = dto.Description,
                Enabled = dto.Enabled ?? true,
                RuntimeType = runtimeType,
                SystemPrompt = dto.SystemPrompt,
                MaxContextTokens = dto.MaxContextTokens.Value,
                StartupTimeout = ParseOptionalLimit(relativeName, "execution_limits.startup_timeout", dto.ExecutionLimits?.StartupTimeout),
                IdleTimeout = ParseOptionalLimit(relativeName, "execution_limits.idle_timeout", dto.ExecutionLimits?.IdleTimeout),
                MaxRunDuration = ParseOptionalLimit(relativeName, "execution_limits.max_run_duration", dto.ExecutionLimits?.MaxRunDuration),
                Cpu = dto.Resources?.Cpu,
                Memory = dto.Resources?.Memory,
                MaxAutomaticRetries = dto.RetryPolicy?.MaxAutomaticRetries,
                InitialBackoff = dto.RetryPolicy?.InitialBackoff is { } backoff
                    ? ParseValue(relativeName, "retry_policy.initial_backoff", backoff, SystemYaml.ParseDurationValue)
                    : null,
                BridgeVersion = dto.Bridge?.Version,
                BridgeHeartbeatInterval = dto.Bridge?.HeartbeatInterval is { } heartbeat
                    ? ParseValue(relativeName, "bridge.heartbeat_interval", heartbeat, SystemYaml.ParseDurationValue)
                    : null,
                BridgeEventCadence = dto.Bridge?.EventCadence is { } cadence
                    ? ParseValue(relativeName, "bridge.event_cadence", cadence, SystemYaml.ParseDurationValue)
                    : null,
                BridgeBehavior = dto.Bridge?.Behavior,
                Skills = dto.Skills,
                Secrets = dto.Secrets,
                Capabilities = dto.Capabilities,
                Mcp = dto.Mcp,
                Tools = dto.Tools,
                Permissions = dto.Permissions,
            });
        }

        return profiles;
    }

    private static TimeSpan? ParseOptionalLimit(string file, string path, string? value)
    {
        if (value is null)
            return null;
        try
        {
            return SystemYaml.ParseDurationValue(value);
        }
        catch (ArgumentException exception)
        {
            throw new ConfigException($"{file}: {path}: {exception.Message}");
        }
    }

    private static T ParseValue<T>(string file, string path, string value, Func<string, T> parse)
    {
        try
        {
            return parse(value);
        }
        catch (ArgumentException exception)
        {
            throw new ConfigException($"{file}: {path}: {exception.Message}");
        }
    }
}
