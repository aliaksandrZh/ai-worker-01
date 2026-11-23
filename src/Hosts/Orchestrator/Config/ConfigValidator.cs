using PiAgent.Orchestrator.Config;
using PiAgent.Platform.Secrets;

namespace PiAgent.Orchestrator.Config;

/// <summary>
/// Cross-knob validation over the parsed configuration plane (system.yaml knobs, profile limits
/// ≤ system constraints, per-profile mandatory max_context_tokens presence/positivity; cross-file
/// references between routing.yaml, repositories.yaml and agents/*. Returns ALL violations;
/// the Config Loader turns a non-empty list into a startup configuration error.
/// </summary>
public static class ConfigValidator
{
    public static IReadOnlyList<string> Validate(ParsedConfig config)
    {
        var violations = new List<string>();
        const string systemFile = "system.yaml";

        void Check(bool ok, string message)
        {
            if (!ok)
                violations.Add(message);
        }

        var system = config.System;
        Check(system.PollInterval >= TimeSpan.FromSeconds(30),
            $"{systemFile}: poll_interval ({Format(system.PollInterval)}) must be ≥ 30s");
        Check(system.PollOverlap > TimeSpan.Zero,
            $"{systemFile}: poll_overlap must be positive");
        Check(system.PerPage is >= 1 and <= 100,
            $"{systemFile}: per_page ({system.PerPage}) must be within the GitHub API maximum of 100");
        Check(system.MaxConcurrentRuns >= 1,
            $"{systemFile}: max_concurrent_runs ({system.MaxConcurrentRuns}) must be ≥ 1");
        Check(system.StartupTimeout > TimeSpan.Zero,
            $"{systemFile}: startup_timeout must be positive");
        Check(system.IdleTimeout > TimeSpan.Zero,
            $"{systemFile}: idle_timeout must be positive");
        Check(system.MaxRunDuration > TimeSpan.Zero,
            $"{systemFile}: max_run_duration must be positive");
        Check(system.HeartbeatInterval > TimeSpan.Zero,
            $"{systemFile}: heartbeat_interval must be positive");
        Check(system.HeartbeatInterval < system.IdleTimeout,
            $"{systemFile}: heartbeat_interval ({Format(system.HeartbeatInterval)}) must be less than idle_timeout ({Format(system.IdleTimeout)})");
        Check(system.MaxLogSize > 0,
            $"{systemFile}: max_log_size must be positive (hard limit)");
        Check(system.MaxArtifactSize > 0,
            $"{systemFile}: max_artifact_size must be positive (hard limit)");
        Check(system.MaxTaskArtifactsSize > 0,
            $"{systemFile}: max_task_artifacts_size must be positive (hard limit)");
        Check(system.MaxAutomaticRetries >= 0,
            $"{systemFile}: max_automatic_retries ({system.MaxAutomaticRetries}) must be ≥ 0");
        Check(system.OutboxRetryWindow > TimeSpan.Zero,
            $"{systemFile}: outbox_retry_window must be positive");
        Check(system.OrphanGracePeriod > TimeSpan.Zero,
            $"{systemFile}: orphan_grace_period must be positive");
        Check(system.OrphanQuarantinePeriod > TimeSpan.Zero,
            $"{systemFile}: orphan_quarantine_period must be positive");

        var profileNames = new HashSet<string>(config.Profiles.Select(p => p.Name), StringComparer.Ordinal);
        var repositoryNames = new HashSet<string>(
            config.Repositories.Select(r => $"{r.Owner}/{r.Name}".ToLowerInvariant()));

        foreach (var profile in config.Profiles)
        {
            var file = $"agents/{profile.Name}/profile.yaml";

            Check(profile.MaxContextTokens > 0,
                $"{file}: max_context_tokens must be positive");

            void CheckLimit(TimeSpan? profileValue, string knob, TimeSpan systemValue)
            {
                if (profileValue is { } value)
                    Check(value <= systemValue,
                        $"{file}: execution_limits.{knob} ({Format(value)}) exceeds the system constraint {knob} ({Format(systemValue)}) — a profile version may lower limits but never exceed them");
            }

            CheckLimit(profile.StartupTimeout, "startup_timeout", system.StartupTimeout);
            CheckLimit(profile.IdleTimeout, "idle_timeout", system.IdleTimeout);
            CheckLimit(profile.MaxRunDuration, "max_run_duration", system.MaxRunDuration);

            if (profile.MaxAutomaticRetries is { } retries)
                Check(retries <= system.MaxAutomaticRetries,
                    $"{file}: retry_policy.max_automatic_retries ({retries}) exceeds the system constraint ({system.MaxAutomaticRetries})");

            if (profile.Cpu is { } cpu)
                Check(cpu > 0, $"{file}: resources.cpu ({cpu}) must be positive");
            if (profile.Memory is { } memory)
            {
                try
                {
                    Check(SizeValue.Parse(memory) > 0, $"{file}: resources.memory ({memory}) must be positive");
                }
                catch (ArgumentException)
                {
                    violations.Add($"{file}: resources.memory ({memory}) must be an integer followed by KiB, MiB, or GiB");
                }
            }

            if (profile.BridgeVersion is not null || profile.BridgeHeartbeatInterval is not null || profile.BridgeEventCadence is not null)
            {
                Check(!string.IsNullOrWhiteSpace(profile.BridgeVersion),
                    $"{file}: bridge.version is required when bridge configuration is present");
                Check(profile.BridgeHeartbeatInterval is null || profile.BridgeHeartbeatInterval.Value > TimeSpan.Zero,
                    $"{file}: bridge.heartbeat_interval must be positive");
                Check(profile.BridgeEventCadence is null || profile.BridgeEventCadence.Value > TimeSpan.Zero,
                    $"{file}: bridge.event_cadence must be positive");
            }
            if (profile.BridgeBehavior is { } behavior)
                Check(behavior is "succeed" or "fail" or "hang"
                    or "never_start" or "permission_denied", // the startup_timeout / permission_denied levers
                    $"{file}: bridge.behavior '{behavior}' must be succeed|fail|hang|never_start|permission_denied (stub scenario lever)");

            // secrets are name-only references to host-managed files; the grammar is also
            // enforced by the Runtime API at plan build (frozen rows) — this is the early check.
            var secretNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var secret in profile.Secrets ?? [])
            {
                var secretError = ProfileSecretName.Validate(secret);
                Check(secretError is null, $"{file}: secrets: {secretError}");
                Check(secretNames.Add(secret), $"{file}: secrets: duplicate reference '{secret}'");
            }
        }

        var ruleScopes = new HashSet<(string? Repo, string TaskType, int Priority)>();
        foreach (var rule in config.RoutingRules)
        {
            var file = "routing.yaml";

            Check(profileNames.Contains(rule.Profile), $"{file}: rule task_type '{rule.TaskType}' references unknown profile '{rule.Profile}'");
            if (rule.Repository is { } repository)
                Check(repositoryNames.Contains(repository.ToLowerInvariant()),
                    $"{file}: rule task_type '{rule.TaskType}' references unknown repository '{repository}' (must exist in repositories.yaml)");
            Check(rule.Priority >= 1, $"{file}: rule task_type '{rule.TaskType}' priority ({rule.Priority}) must be ≥ 1");

            var scope = (rule.Repository?.ToLowerInvariant(), rule.TaskType, rule.Priority);
            Check(ruleScopes.Add(scope),
                $"{file}: duplicate routing rule for repository '{rule.Repository ?? "global"}', task_type '{rule.TaskType}', priority {rule.Priority}");

        }

        foreach (var repository in config.Repositories)
        {
            var file = "repositories.yaml";
            if (repository.MinimumPermission is { } permission)
                Check(permission == "write",
                    $"{file}: repository {repository.Owner}/{repository.Name} minimum_permission '{permission}' is not supported (write is the baseline)");
        }

        return violations;
    }

    private static string Format(TimeSpan value) => value.ToString(@"d\.hh\:mm\:ss");
}
