using PiAgent.Orchestrator.Config;
using Xunit;

namespace PiAgent.Hosts.Tests.Config;

/// <summary>
/// §16 cross-knob validation for the parsed configuration plane; every violation is a diagnosable
/// startup error naming file, YAML path and rule. Validation aggregates all violations.
/// </summary>
public class ConfigValidatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cfgv").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private IReadOnlyList<string> ValidateSample(
        string system = ConfigSamples.System,
        string repositories = ConfigSamples.Repositories,
        string routing = ConfigSamples.Routing,
        string plannerProfile = ConfigSamples.PlannerProfile)
    {
        var root = Directory.CreateTempSubdirectory("cfgs").FullName;
        try
        {
            ConfigSamples.WriteSampleTree(root, system, repositories, routing, plannerProfile);
            return ConfigValidator.Validate(ConfigParser.Parse(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ValidSample_Passes()
    {
        var violations = ValidateSample();
        Assert.Empty(violations);
    }

    [Fact]
    public void PollInterval_BelowMinimum_Fails()
    {
        var violations = ValidateSample(system: ConfigSamples.System.Replace("poll_interval: 60s", "poll_interval: 10s"));
        var violation = Assert.Single(violations);
        Assert.Contains("system.yaml", violation);
        Assert.Contains("poll_interval", violation);
        Assert.Contains("30s", violation);
    }

    [Theory]
    [InlineData("poll_overlap: 0s")]
    [InlineData("per_page: 0")]
    [InlineData("per_page: 101")]
    [InlineData("max_concurrent_runs: 0")]
    [InlineData("startup_timeout: 0s")]
    [InlineData("idle_timeout: 0s")]
    [InlineData("max_run_duration: 0s")]
    [InlineData("heartbeat_interval: 0s")]
    [InlineData("max_log_size: 0KiB")]
    [InlineData("max_artifact_size: 0KiB")]
    [InlineData("max_task_artifacts_size: 0KiB")]
    [InlineData("max_automatic_retries: -1")]
    [InlineData("outbox_retry_window: 0s")]
    [InlineData("orphan_grace_period: 0s")]
    [InlineData("orphan_quarantine_period: 0s")]
    public void NonPositiveKnob_Fails(string replacement)
    {
        var originalKey = replacement[..replacement.IndexOf(':')];
        var violations = ValidateSample(
            system: ReplaceLine(ConfigSamples.System, originalKey, replacement));
        Assert.NotEmpty(violations);
        Assert.Contains(violations, v => v.Contains("system.yaml") && v.Contains(originalKey));
    }

    [Fact]
    public void HeartbeatInterval_GreaterOrEqualIdleTimeout_Fails()
    {
        var system = ReplaceLine(ReplaceLine(ConfigSamples.System, "heartbeat_interval", "heartbeat_interval: 60m"), "idle_timeout", "idle_timeout: 60m");
        var violations = ValidateSample(system: system);
        var violation = Assert.Single(violations);
        Assert.Contains("heartbeat_interval", violation);
        Assert.Contains("idle_timeout", violation);
    }

    [Fact]
    public void ProfileLimits_ExceedingSystem_Fails()
    {
        var profile = ReplaceLine(ConfigSamples.PlannerProfile, "idle_timeout", "idle_timeout: 90m");
        var violations = ValidateSample(plannerProfile: profile);
        var violation = Assert.Single(violations);
        Assert.Contains("agents/planner/profile.yaml", violation);
        Assert.Contains("idle_timeout", violation);
    }

    [Fact]
    public void ProfileRetries_ExceedingSystem_Fails()
    {
        var profile = ReplaceLine(ConfigSamples.PlannerProfile, "max_automatic_retries", "max_automatic_retries: 2");
        var violations = ValidateSample(plannerProfile: profile);
        var violation = Assert.Single(violations);
        Assert.Contains("max_automatic_retries", violation);
    }

    [Fact]
    public void ProfileResources_NonPositiveOrUnparsable_Fails()
    {
        var violations = ValidateSample(plannerProfile: ConfigSamples.PlannerProfile.Replace("cpu: 2.0", "cpu: 0"));
        Assert.Single(violations);

        violations = ValidateSample(plannerProfile: ConfigSamples.PlannerProfile.Replace("memory: 2GiB", "memory: 0GiB"));
        Assert.Single(violations);

        violations = ValidateSample(plannerProfile: ConfigSamples.PlannerProfile.Replace("memory: 2GiB", "memory: huge"));
        var violation = Assert.Single(violations);
        Assert.Contains("memory", violation);
    }

    [Fact]
    public void ProfileBridge_WithoutVersion_Fails()
    {
        var violations = ValidateSample(plannerProfile: ConfigSamples.PlannerProfile.Replace("  version: \"1.0.0\"\n", ""));
        var violation = Assert.Single(violations);
        Assert.Contains("bridge", violation);
        Assert.Contains("version", violation);
    }

    [Fact]
    public void RoutingRule_UnknownProfile_Fails()
    {
        var routing = ConfigSamples.Routing.Replace("profile: planner", "profile: ghost");
        var violations = ValidateSample(routing: routing);
        var violation = Assert.Single(violations);
        Assert.Contains("routing.yaml", violation);
        Assert.Contains("ghost", violation);
    }

    [Fact]
    public void RoutingRule_UnknownRepository_Fails()
    {
        var routing = """
            rules:
              - task_type: plan
                repository: missing-org/missing-repo
                profile: planner
            """;
        var violations = ValidateSample(routing: routing);
        var violation = Assert.Single(violations);
        Assert.Contains("routing.yaml", violation);
        Assert.Contains("missing-org/missing-repo", violation);
    }

    [Fact]
    public void RoutingRule_DuplicateScopePriority_Fails()
    {
        var routing = """
rules:
  - task_type: plan
    profile: planner
    priority: 1
    enabled: true
  - task_type: plan
    profile: planner
    priority: 1
    enabled: true
""";
        var violations = ValidateSample(routing: routing);
        var violation = Assert.Single(violations);
        Assert.Contains("duplicate", violation);
        Assert.Contains("plan", violation);
    }

    [Fact]
    public void RoutingRule_NonPositivePriority_Fails()
    {
        var routing = ConfigSamples.Routing.Replace("priority: 1", "priority: 0");
        var violations = ValidateSample(routing: routing);
        var violation = Assert.Single(violations);
        Assert.Contains("priority", violation);
    }

    [Fact]
    public void RepositoryMinimumPermission_UnexpectedValue_Fails()
    {
        var repositories = ConfigSamples.Repositories.Replace("repositories: []", """
            repositories:
              - owner: example-org
                name: example-repo
                authorization_policy:
                  minimum_permission: admin
            """);
        var violations = ValidateSample(repositories: repositories);
        var violation = Assert.Single(violations);
        Assert.Contains("minimum_permission", violation);
        Assert.Contains("write", violation);
    }

    [Fact]
    public void MultipleViolations_Aggregated()
    {
        var system = ReplaceLine(ReplaceLine(ConfigSamples.System, "poll_interval", "poll_interval: 10s"), "per_page", "per_page: 101");
        var violations = ValidateSample(system: system);
        Assert.Equal(2, violations.Count);
    }

    // ---- profile secret references (decision 54; ticket 11b) ----

    [Fact]
    public void ProfileSecret_ReservedName_Fails() // collision with the per-Run callback credential (decision 60)
    {
        var violations = ValidateSample(plannerProfile: ReplaceLine(
            ConfigSamples.PlannerProfile, "secrets", "secrets: ['callback-token']"));
        var violation = Assert.Single(violations);
        Assert.Contains("agents/planner/profile.yaml", violation);
        Assert.Contains("callback-token", violation);
        Assert.Contains("reserved for the per-Run callback credential", violation);
    }

    [Fact]
    public void ProfileSecret_TraversalName_Fails()
    {
        var violations = ValidateSample(plannerProfile: ReplaceLine(
            ConfigSamples.PlannerProfile, "secrets", "secrets: ['../github-sync-pat.txt']"));
        var violation = Assert.Single(violations);
        Assert.Contains("must match [A-Za-z0-9][A-Za-z0-9._-]*", violation);
    }

    [Fact]
    public void ProfileSecret_EmptyName_Fails()
    {
        var violations = ValidateSample(plannerProfile: ReplaceLine(
            ConfigSamples.PlannerProfile, "secrets", "secrets: ['   ']"));
        var violation = Assert.Single(violations);
        Assert.Contains("non-empty", violation);
    }

    [Fact]
    public void ProfileSecret_DuplicateReference_Fails()
    {
        var violations = ValidateSample(plannerProfile: ReplaceLine(
            ConfigSamples.PlannerProfile, "secrets", "secrets: ['github_agent_pat', 'github_agent_pat']"));
        var violation = Assert.Single(violations);
        Assert.Contains("duplicate reference 'github_agent_pat'", violation);
    }

    [Fact]
    public void ProfileSecret_ValidNames_Pass()
    {
        var violations = ValidateSample(plannerProfile: ReplaceLine(
            ConfigSamples.PlannerProfile, "secrets", "secrets: ['github_agent_pat', 'mcp-github.token-v2']"));
        Assert.Empty(violations);
    }

    private static string ReplaceLine(string text, string key, string replacement)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith(key + ":"))
            {
                // preserve the line's indentation — replacements must stay inside their YAML mapping
                var indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
                lines[i] = indent + replacement;
                break;
            }
        }
        return string.Join('\n', lines);
    }
}
