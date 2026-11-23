using PiAgent.Orchestrator.Config;
using Xunit;

namespace PiAgent.Hosts.Tests.Config;

/// <summary>
/// Config-plane YAML parsing (task 02): strict schema, unit-string values, diagnosable errors
/// naming file and path, and the decision-49/§21 distinctions (allowlist absent vs empty).
/// Fixtures write temp-dir config trees; ConfigParser.Parse(configRoot) is the public surface.
/// </summary>
public class ConfigParserTests : IDisposable
{
    private readonly string _root;

    public ConfigParserTests() => _root = Directory.CreateTempSubdirectory("cfg").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private const string MinimalSystem = """
        github:
          bot_login: pi-bot
        """;

    private const string MinimalRepositories = "repositories: []\n";

    private const string MinimalRouting = "rules: []\n";

    private void WriteMinimalAll()
    {
        Write("system.yaml", MinimalSystem);
        Write("repositories.yaml", MinimalRepositories);
        Write("routing.yaml", MinimalRouting);
        Directory.CreateDirectory(Path.Combine(_root, "agents"));
    }

    // --- system.yaml ---

    [Fact]
    public void ParsedSystem_AllDefaults_WhenKeysAbsent()
    {
        WriteMinimalAll();
        var config = ConfigParser.Parse(_root);
        Assert.Equal("pi-bot", config.System.BotLogin);
        Assert.Equal(TimeSpan.FromSeconds(60), config.System.PollInterval);
        Assert.Equal(TimeSpan.FromMinutes(5), config.System.PollOverlap);
        Assert.Equal(100, config.System.PerPage);
        Assert.Equal(10, config.System.MaxConcurrentRuns);
        Assert.Equal(TimeSpan.FromMinutes(10), config.System.StartupTimeout);
        Assert.Equal(TimeSpan.FromMinutes(60), config.System.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(4), config.System.MaxRunDuration);
        Assert.Equal(TimeSpan.FromSeconds(30), config.System.HeartbeatInterval);
        Assert.Equal(52_428_800, config.System.MaxLogSize);
        Assert.Equal(262_144, config.System.MaxArtifactSize);
        Assert.Equal(524_288, config.System.MaxTaskArtifactsSize);
        Assert.Equal(1, config.System.MaxAutomaticRetries);
        Assert.Equal(TimeSpan.FromHours(24), config.System.OutboxRetryWindow);
        Assert.Equal(TimeSpan.FromHours(24), config.System.OrphanGracePeriod);
        Assert.Equal(TimeSpan.FromHours(24), config.System.OrphanQuarantinePeriod);
        Assert.Empty(config.Repositories);
        Assert.Empty(config.RoutingRules);
        Assert.Empty(config.Profiles);
    }

    [Fact]
    public void ParsedSystem_Units_Parsed()
    {
        Write("system.yaml", """
            github:
              bot_login: pi-bot
            poll_interval: 45s
            poll_overlap: 7m
            startup_timeout: 2m
            idle_timeout: 90m
            max_run_duration: 6h
            heartbeat_interval: 15s
            max_log_size: 64MiB
            max_artifact_size: 512KiB
            max_task_artifacts_size: 1MiB
            outbox_retry_window: 12h
            orphan_grace_period: 48h
            orphan_quarantine_period: 24h
            per_page: 50
            max_concurrent_runs: 3
            max_automatic_retries: 0
            """);
        Write("repositories.yaml", MinimalRepositories);
        Write("routing.yaml", MinimalRouting);
        Directory.CreateDirectory(Path.Combine(_root, "agents"));
        var system = ConfigParser.Parse(_root).System;
        Assert.Equal(TimeSpan.FromSeconds(45), system.PollInterval);
        Assert.Equal(TimeSpan.FromMinutes(7), system.PollOverlap);
        Assert.Equal(TimeSpan.FromMinutes(2), system.StartupTimeout);
        Assert.Equal(TimeSpan.FromMinutes(90), system.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(6), system.MaxRunDuration);
        Assert.Equal(TimeSpan.FromSeconds(15), system.HeartbeatInterval);
        Assert.Equal(67_108_864, system.MaxLogSize);
        Assert.Equal(524_288, system.MaxArtifactSize);
        Assert.Equal(1_048_576, system.MaxTaskArtifactsSize);
        Assert.Equal(TimeSpan.FromHours(12), system.OutboxRetryWindow);
        Assert.Equal(TimeSpan.FromHours(48), system.OrphanGracePeriod);
        Assert.Equal(TimeSpan.FromHours(24), system.OrphanQuarantinePeriod);
        Assert.Equal(50, system.PerPage);
        Assert.Equal(3, system.MaxConcurrentRuns);
        Assert.Equal(0, system.MaxAutomaticRetries);
    }

    [Fact]
    public void ParseDurationValue_BadUnit_ThrowsWithValueInMessage()
    {
        var exception = Assert.Throws<ArgumentException>(() => PiAgent.Platform.Config.SystemYaml.ParseDurationValue("10min"));
        Assert.Contains("10min", exception.Message);
    }

    [Fact]
    public void SizeValue_UnknownUnit_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => SizeValue.Parse("50MB"));
        Assert.Contains("50MB", exception.Message);
    }

    [Fact]
    public void SystemYaml_NoBotLogin_FailsNamingFileAndPath()
    {
        Write("system.yaml", "poll_interval: 60s\n");
        Write("repositories.yaml", MinimalRepositories);
        Write("routing.yaml", MinimalRouting);
        var exception = Assert.Throws<ConfigException>(() => ConfigParser.Parse(_root));
        Assert.Contains("system.yaml", exception.Message);
        Assert.Contains("github", exception.Message);
        Assert.Contains("bot_login", exception.Message);
    }

    [Fact]
    public void SystemYaml_UnknownKey_FailsNamingKey()
    {
        Write("system.yaml", "github:\n  bot_login: pi-bot\nmystery_knob: 5\n");
        Write("repositories.yaml", MinimalRepositories);
        Write("routing.yaml", MinimalRouting);
        var exception = Assert.Throws<ConfigException>(() => ConfigParser.Parse(_root));
        Assert.Contains("system.yaml", exception.Message);
        Assert.Contains("mystery_knob", exception.Message);
    }

    [Fact]
    public void SystemYaml_Missing_Fails()
    {
        Write("repositories.yaml", MinimalRepositories);
        Write("routing.yaml", MinimalRouting);
        var exception = Assert.Throws<ConfigException>(() => ConfigParser.Parse(_root));
        Assert.Contains("system.yaml", exception.Message);
    }

    [Fact]
    public void SystemYaml_DuplicateKey_FailsDiagnosable()
    {
        Write("system.yaml", "github:\n  bot_login: pi-bot\npoll_interval: 60s\npoll_interval: 90s\n");
        Write("repositories.yaml", MinimalRepositories);
        Write("routing.yaml", MinimalRouting);
        Directory.CreateDirectory(Path.Combine(_root, "agents"));
        var exception = Assert.ThrowsAny<Exception>(() => ConfigParser.Parse(_root));
        Assert.Contains("system.yaml", exception.Message);
    }

    [Fact]
    public void SystemYaml_YamlSyntaxError_MessagesNameFile()
    {
        Write("system.yaml", "github: [unclosed\n");
        Write("repositories.yaml", MinimalRepositories);
        Write("routing.yaml", MinimalRouting);
        var exception = Assert.Throws<ConfigException>(() => ConfigParser.Parse(_root));
        Assert.Contains("system.yaml", exception.Message);
    }

    // --- repositories.yaml ---

    [Fact]
    public void Repositories_EntryMissingOwner_Fails()
    {
        Write("system.yaml", MinimalSystem);
        Write("repositories.yaml", "repositories:\n  - name: repo-one\n");
        Write("routing.yaml", MinimalRouting);
        var exception = Assert.Throws<ConfigException>(() => ConfigParser.Parse(_root));
        Assert.Contains("repositories.yaml", exception.Message);
        Assert.Contains("owner", exception.Message);
    }

    [Fact]
    public void Repositories_DuplicateFullName_Fails()
    {
        Write("system.yaml", MinimalSystem);
        Write("repositories.yaml", """
            repositories:
              - owner: example-org
                name: repo-one
              - owner: EXAMPLE-org
                name: repo-one
            """);
        Write("routing.yaml", MinimalRouting);
        var exception = Assert.Throws<ConfigException>(() => ConfigParser.Parse(_root));
        Assert.Contains("example-org/repo-one", exception.Message);
    }

    [Fact]
    public void Allowlist_AbsentVsEmpty()
    {
        Write("system.yaml", MinimalSystem);
        Write("repositories.yaml", """
            repositories:
              - owner: example-org
                name: repo-one
                allowlist:
                  - alice
              - owner: example-org
                name: repo-two
                allowlist: []
              - owner: example-org
                name: repo-three
            """);
        Write("routing.yaml", MinimalRouting);
        Directory.CreateDirectory(Path.Combine(_root, "agents"));
        var config = ConfigParser.Parse(_root);
        var byName = config.Repositories.ToDictionary(r => r.Name);
        Assert.NotNull(byName["repo-one"].AllowedLogins);
        Assert.Equal(["alice"], byName["repo-one"].AllowedLogins);
        Assert.Empty(byName["repo-two"].AllowedLogins!);
        Assert.Null(byName["repo-three"].AllowedLogins);
    }

    [Fact]
    public void Repositories_FullEntry_Parses()
    {
        Write("system.yaml", MinimalSystem);
        Write("repositories.yaml", """
            repositories:
              - owner: example-org
                name: repo-one
                enabled: false
                permitted_commands: [plan, status, stop, help, retry]
                permitted_work_item_types: [issue]
                authorization_policy:
                  minimum_permission: write
            """);
        Write("routing.yaml", MinimalRouting);
        Directory.CreateDirectory(Path.Combine(_root, "agents"));
        var entry = ConfigParser.Parse(_root).Repositories.Single();
        Assert.Equal("example-org", entry.Owner);
        Assert.Equal("repo-one", entry.Name);
        Assert.False(entry.Enabled);
        Assert.Equal(["plan", "status", "stop", "help", "retry"], entry.PermittedCommands);
        Assert.Equal(["issue"], entry.PermittedWorkItemTypes);
        Assert.Equal("write", entry.MinimumPermission);
    }

    [Fact]
    public void RepositoriesYaml_Missing_Fails()
    {
        Write("system.yaml", MinimalSystem);
        Write("routing.yaml", MinimalRouting);
        Directory.CreateDirectory(Path.Combine(_root, "agents"));
        var exception = Assert.Throws<ConfigException>(() => ConfigParser.Parse(_root));
        Assert.Contains("repositories.yaml", exception.Message);
    }

    // --- routing.yaml ---

    [Fact]
    public void Routing_UnknownTaskType_Fails()
    {
        Write("system.yaml", MinimalSystem);
        Write("repositories.yaml", MinimalRepositories);
        Write("routing.yaml", "rules:\n  - task_type: refactor\n    profile: planner\n");
        var exception = Assert.Throws<ConfigException>(() => ConfigParser.Parse(_root));
        Assert.Contains("routing.yaml", exception.Message);
        Assert.Contains("refactor", exception.Message);
    }

    [Fact]
    public void Routing_RuleDefaults_Parsed()
    {
        Write("system.yaml", MinimalSystem);
        Write("repositories.yaml", MinimalRepositories);
        Write("routing.yaml", """
            rules:
              - task_type: plan
                profile: planner
            """);
        Directory.CreateDirectory(Path.Combine(_root, "agents"));
        var rule = ConfigParser.Parse(_root).RoutingRules.Single();
        Assert.Equal("plan", rule.TaskType);
        Assert.Equal("planner", rule.Profile);
        Assert.Null(rule.Repository);
        Assert.Equal(1, rule.Priority);
        Assert.True(rule.Enabled);
    }

    [Fact]
    public void RoutingYaml_Missing_Fails()
    {
        Write("system.yaml", MinimalSystem);
        Write("repositories.yaml", MinimalRepositories);
        Assert.Throws<ConfigException>(() => ConfigParser.Parse(_root));
    }

    // --- profiles ---

    [Fact]
    public void Profile_MissingMaxContextTokens_Fails()
    {
        Write("system.yaml", MinimalSystem);
        Write("repositories.yaml", MinimalRepositories);
        Write("routing.yaml", MinimalRouting);
        Directory.CreateDirectory(Path.Combine(_root, "agents"));
        Write("agents/planner/profile.yaml", "description: analyze things\n");
        var exception = Assert.Throws<ConfigException>(() => ConfigParser.Parse(_root));
        Assert.Contains("planner", exception.Message);
        Assert.Contains("max_context_tokens", exception.Message);
    }

    [Fact]
    public void Profile_FullEntry_Parsed()
    {
        Write("system.yaml", MinimalSystem);
        Write("repositories.yaml", MinimalRepositories);
        Write("routing.yaml", MinimalRouting);
        Write("agents/planner/profile.yaml", """
            description: Analyze requirements and repository; identify gaps; prepare an implementation plan
            enabled: true
            runtime_type: pi
            max_context_tokens: 200000
            execution_limits:
              startup_timeout: 5m
              idle_timeout: 30m
              max_run_duration: 2h
            resources:
              cpu: 2.0
              memory: 2GiB
            retry_policy:
              max_automatic_retries: 1
              initial_backoff: 30s
            bridge:
              version: "1.0.0"
              heartbeat_interval: 15s
              event_cadence: 5s
            system_prompt: |
              You are the planner agent.
            secrets: [github_agent_pat]
            capabilities: [read_repository]
            skills: [github-api]
            mcp:
              servers: []
            tools:
              allowed: []
            """);
        var profile = ConfigParser.Parse(_root).Profiles.Single();
        Assert.Equal("planner", profile.Name);
        Assert.StartsWith("Analyze requirements", profile.Description);
        Assert.True(profile.Enabled);
        Assert.Equal("pi", profile.RuntimeType);
        Assert.Equal(200000, profile.MaxContextTokens);
        Assert.Equal(TimeSpan.FromMinutes(5), profile.StartupTimeout);
        Assert.Equal(TimeSpan.FromMinutes(30), profile.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(2), profile.MaxRunDuration);
        Assert.Equal(2.0m, profile.Cpu);
        Assert.Equal("2GiB", profile.Memory);
        Assert.Equal(1, profile.MaxAutomaticRetries);
        Assert.Equal(TimeSpan.FromSeconds(30), profile.InitialBackoff);
        Assert.Equal("1.0.0", profile.BridgeVersion);
        Assert.Equal(TimeSpan.FromSeconds(15), profile.BridgeHeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), profile.BridgeEventCadence);
        Assert.StartsWith("You are the planner", profile.SystemPrompt);
        Assert.Equal(["github_agent_pat"], profile.Secrets);
        Assert.Equal(["read_repository"], profile.Capabilities);
        Assert.Equal(["github-api"], profile.Skills);
        Assert.NotNull(profile.Mcp);
        Assert.NotNull(profile.Tools);
    }

    [Fact]
    public void Profile_RuntimeTypeNotPi_Fails()
    {
        Write("system.yaml", MinimalSystem);
        Write("repositories.yaml", MinimalRepositories);
        Write("routing.yaml", MinimalRouting);
        Write("agents/planner/profile.yaml", "max_context_tokens: 100\nruntime_type: codex\n");
        var exception = Assert.Throws<ConfigException>(() => ConfigParser.Parse(_root));
        Assert.Contains("runtime_type", exception.Message);
        Assert.Contains("codex", exception.Message);
    }

    [Fact]
    public void Profile_UnknownNestedKey_Fails()
    {
        Write("system.yaml", MinimalSystem);
        Write("repositories.yaml", MinimalRepositories);
        Write("routing.yaml", MinimalRouting);
        Write("agents/planner/profile.yaml", "max_context_tokens: 100\nexecution_limits:\n  startup_timeout: 5m\n  warp_speed: true\n");
        var exception = Assert.Throws<ConfigException>(() => ConfigParser.Parse(_root));
        Assert.Contains("warp_speed", exception.Message);
    }

    [Fact]
    public void Profile_NameComesFromDirectory()
    {
        Write("system.yaml", MinimalSystem);
        Write("repositories.yaml", MinimalRepositories);
        Write("routing.yaml", MinimalRouting);
        Write("agents/coder/profile.yaml", "max_context_tokens: 100\n");
        Assert.Equal("coder", ConfigParser.Parse(_root).Profiles.Single().Name);
    }

    [Fact]
    public void AgentsDir_Empty_ZeroProfiles()
    {
        Write("system.yaml", MinimalSystem);
        Write("repositories.yaml", MinimalRepositories);
        Write("routing.yaml", MinimalRouting);
        Directory.CreateDirectory(Path.Combine(_root, "agents"));
        Assert.Empty(ConfigParser.Parse(_root).Profiles);
    }

    [Fact]
    public void AgentsDir_Missing_Fails()
    {
        Write("system.yaml", MinimalSystem);
        Write("repositories.yaml", MinimalRepositories);
        Write("routing.yaml", MinimalRouting);
        var exception = Assert.Throws<ConfigException>(() => ConfigParser.Parse(_root));
        Assert.Contains("agents", exception.Message);
    }

    [Fact]
    public void AgentSubdirectoryWithoutProfileYaml_Fails()
    {
        Write("system.yaml", MinimalSystem);
        Write("repositories.yaml", MinimalRepositories);
        Write("routing.yaml", MinimalRouting);
        Directory.CreateDirectory(Path.Combine(_root, "agents", "planner"));
        var exception = Assert.Throws<ConfigException>(() => ConfigParser.Parse(_root));
        Assert.Contains("planner", exception.Message);
    }

    /// <summary>Shipped configuration integrity (ticket 12): the real shipped tree (deploy/config yamls
    /// + repo-root agents/ profiles) must parse under the strict schema — a dropped key in a
    /// profile.yaml aborts the Orchestrator at startup otherwise, which CI should catch, not the next
    /// stack start. Skipped when the layout is absent (packaged CI).</summary>
    [Fact]
    public void ShippedConfig_ParsesStrictly()
    {
        var configRoot = FindShippedConfigRoot();
        if (configRoot is null)
            return;
        try
        {
            var parsed = ConfigParser.Parse(configRoot);
            Assert.Contains(parsed.Profiles, p => p.Name == "planner");
            var planner = parsed.Profiles.Single(p => p.Name == "planner");
            Assert.Equal(["github-write-pat.txt"], planner.Secrets); // decision 54/50: the repo write PAT reference
            Assert.Equal("1.2.0", planner.BridgeVersion); // ticket 13 bumped the shared bridge (permission_blocked recording)
            // ticket 19: the reviewer profile is shipped beside the planner and parses under the same strict schema
            var reviewer = parsed.Profiles.Single(p => p.Name == "reviewer");
            Assert.Equal(["github-write-pat.txt"], reviewer.Secrets);
            Assert.Equal("1.2.0", reviewer.BridgeVersion);
        }
        finally
        {
            // the split layout materializes a temporary merged root — clean it up
            if (Path.GetFileName(configRoot).StartsWith("pi-agent-shipped-config-", StringComparison.Ordinal))
                Directory.Delete(configRoot, true);
        }
    }

    /// <summary>
    /// Locates the shipped configuration in the repo layout the parser expects: a config root whose
    /// `agents/` subdirectory holds the profiles. Two shapes are supported: a single-root tree
    /// (`<dir>/config` with both the yamls and `agents/` inside) and the split layout (yamls in
    /// `deploy/config`, profiles in the repo-root `agents/`). For the split layout a temporary merged
    /// root is materialized with symlinks (the parser resolves paths under one root); on platforms
    /// without symlink support the test simply skips.
    /// </summary>
    private static string? FindShippedConfigRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent!)
        {
            // single-root shape: `<dir>/config` carries the yamls and agents/ together
            var candidate = Path.Combine(directory.FullName, "config");
            if (File.Exists(Path.Combine(candidate, "system.yaml")) &&
                Directory.Exists(Path.Combine(candidate, "agents")))
                return candidate;

            // split shape: yamls in `deploy/config`, profiles in the sibling repo-root `agents/`
            var yamlRoot = Path.Combine(directory.FullName, "deploy", "config");
            var agentsRoot = Path.Combine(directory.FullName, "agents");
            if (File.Exists(Path.Combine(yamlRoot, "system.yaml")) &&
                Directory.Exists(Path.Combine(agentsRoot, "planner")))
            {
                try
                {
                    var merged = Path.Combine(Path.GetTempPath(), $"pi-agent-shipped-config-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(merged);
                    foreach (var yaml in new[] { "system.yaml", "repositories.yaml", "routing.yaml" })
                        File.Copy(Path.Combine(yamlRoot, yaml), Path.Combine(merged, yaml));
                    Directory.CreateSymbolicLink(Path.Combine(merged, "agents"), agentsRoot);
                    return merged;
                }
                catch (IOException)
                {
                    return null; // no symlink support on this platform — skip the shipped-tree test
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }
            }

            // legacy fallback: the pre-restructure src/config single-root tree
            var srcCandidate = Path.Combine(directory.FullName, "src", "config");
            if (File.Exists(Path.Combine(srcCandidate, "system.yaml")))
                return srcCandidate;
        }
        return null;
    }
}
