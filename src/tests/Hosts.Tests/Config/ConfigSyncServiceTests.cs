using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Config;
using Xunit;

namespace PiAgent.Hosts.Tests.Config;

/// <summary>
/// ConfigSyncService over real postgres: one transaction (no partial rows), decision-49/§21
/// configuration JSON, decision-53 cursors, decision-81 version dedup/rule re-point, and the
/// owner-approved resync semantics (pause/remove/disable).
/// </summary>
public class ConfigSyncServiceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string OneRepo = """
        repositories:
          - owner: example-org
            name: example-repo
        """;

    private const string Routing = """
        rules:
          - task_type: plan
            profile: planner
        """;

    private string _lastRoot = string.Empty;

    private ParsedConfig Parse(string repositoriesYaml = OneRepo, string routingYaml = Routing, string plannerYaml = ConfigSamples.PlannerProfile)
    {
        var root = Directory.CreateTempSubdirectory("sync").FullName;
        _lastRoot = root;
        ConfigSamples.WriteSampleTree(root,
            repositories: repositoriesYaml,
            routing: routingYaml,
            plannerProfile: plannerYaml);
        return ConfigParser.Parse(root);
    }

    private static IReadOnlyList<ProfileSnapshot> SnapshotsFor(ParsedConfig config, string configRoot, string? sourceCommitSha = null) =>
        config.Profiles
            .Select(profile => ConfigSnapshotBuilder.Build(profile, Path.Combine(configRoot, "agents", profile.Name), sourceCommitSha))
            .ToList();

    private static ConfigSynchronization Sync(ParsedConfig config, string configRoot, string? sourceCommitSha = null)
    {
        var repoInfos = config.Repositories.ToDictionary(
            r => $"{r.Owner}/{r.Name}".ToLowerInvariant(),
            r => new GitHubRepoInfo(FakeRepoId(r), $"{r.Owner}/{r.Name}".ToLowerInvariant(), "develop"));
        return new ConfigSynchronization(config, repoInfos, SnapshotsFor(config, configRoot, sourceCommitSha), sourceCommitSha);
    }

    /// <summary>Deterministic fake external id per owner/name so duplicate-config tests can force collisions.</summary>
    private static long FakeRepoId(RepositoryEntry entry)
    {
        unchecked
        {
            return 1000 + Math.Abs($"{entry.Owner}/{entry.Name}".GetHashCode()) % 900_000;
        }
    }

    private static JsonDocument Json(string? raw) => JsonDocument.Parse(raw!);

    // --- onboarding ---

    [Fact]
    public async Task Onboarding_CreatesRepositoryCursorsProfileVersionRules()
    {
        await postgres.ResetAsync();
        var config = Parse();
        await using var db = postgres.NewDbContext();
        var sync = Sync(config, _lastRoot, "abc123");
        var result = await new ConfigSyncService(db).SyncAsync(sync);

        Assert.Equal(1, result.RepositoriesCreated);
        Assert.Equal(7, result.CursorsCreated); // six decision-53 stream families + the deleted-comment sweep row
        Assert.Equal(1, result.VersionsAdded);
        Assert.Equal(1, result.RoutingRulesUpserted);

        var repository = await db.Repositories.AsNoTracking().SingleAsync();
        Assert.Equal("example-org", repository.Owner);
        Assert.Equal("example-repo", repository.Name);
        Assert.Equal("example-org/example-repo", repository.FullName);
        Assert.True(repository.IsEnabled);
        var info = sync.Repositories["example-org/example-repo"];
        Assert.Equal(info.Id, repository.GithubRepositoryId);
        using (var configuration = Json(repository.Configuration))
        {
            var allowlist = configuration.RootElement.GetProperty("allowlist");
            Assert.True(allowlist.GetProperty("enabled").GetBoolean());
            Assert.Equal(["example-org"], allowlist.GetProperty("logins").EnumerateArray().Select(e => e.GetString()));
            Assert.True(configuration.RootElement.GetProperty("permitted_commands").GetArrayLength() > 0);
            Assert.Equal("write", configuration.RootElement.GetProperty("authorization").GetProperty("minimum_permission").GetString());
        }

        var cursors = await db.SyncCursors.AsNoTracking().Where(c => c.RepositoryId == repository.Id).ToListAsync();
        Assert.Equal(7, cursors.Count);
        Assert.Equal(
            SyncCursorStreams.All.ToHashSet(),
            cursors.Select(c => c.Stream).ToHashSet());
        Assert.All(cursors, c => Assert.Null(c.CursorValue));

        var profile = await db.AgentProfiles.AsNoTracking().SingleAsync();
        Assert.Equal("planner", profile.Name);
        Assert.True(profile.IsEnabled);

        var version = await db.AgentProfileVersions.AsNoTracking().SingleAsync();
        Assert.Equal(profile.Id, version.AgentProfileId);
        Assert.Equal(1, version.Version);
        Assert.Equal(ProfileVersionStates.Draft, version.State);
        Assert.Equal("abc123", version.SourceCommitSha);
        Assert.Equal("config/agents/planner", version.SourceDirectory);
        Assert.Equal(info.DefaultBranch, repository.DefaultBranch);
        Assert.Equal(200000, version.MaxContextTokens);
        Assert.Equal(TimeSpan.FromMinutes(10), version.StartupTimeout);
        Assert.Equal(TimeSpan.FromMinutes(60), version.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(4), version.MaxRunDuration);
        Assert.Null(version.ContainerImage);
        Assert.Null(version.ImageDigest);
        using (var bridge = Json(version.BridgeConfiguration))
        {
            Assert.Equal("1.0.0", bridge.RootElement.GetProperty("version").GetString());
            Assert.Equal(TimeSpan.FromSeconds(15).ToString(), bridge.RootElement.GetProperty("heartbeat_interval").GetString());
            Assert.Equal(TimeSpan.FromSeconds(5).ToString(), bridge.RootElement.GetProperty("event_cadence").GetString());
        }
        using (var resources = Json(version.ResourceConfiguration))
        {
            Assert.Equal(2.0m, resources.RootElement.GetProperty("cpu").GetDecimal());
            Assert.Equal("2GiB", resources.RootElement.GetProperty("memory").GetString());
        }
        using (var retry = Json(version.RetryPolicy))
        {
            Assert.Equal(1, retry.RootElement.GetProperty("max_automatic_retries").GetInt32());
        }

        var rule = await db.AgentProfileRoutingRules.AsNoTracking().SingleAsync();
        Assert.Null(rule.RepositoryId);
        Assert.Equal("plan", rule.TaskType);
        Assert.Equal(version.Id, rule.AgentProfileVersionId);
        Assert.Equal(1, rule.Priority);
        Assert.True(rule.IsEnabled);
    }

    [Fact]
    public async Task Onboarding_ExplicitEmptyAllowlist_DisablesFiltering()
    {
        await postgres.ResetAsync();
        var config = Parse(repositoriesYaml: """
            repositories:
              - owner: example-org
                name: example-repo
                allowlist: []
            """);
        await using var db = postgres.NewDbContext();
        await new ConfigSyncService(db).SyncAsync(Sync(config, _lastRoot));
        var repository = await db.Repositories.AsNoTracking().SingleAsync();
        using var configuration = Json(repository.Configuration);
        Assert.False(configuration.RootElement.GetProperty("allowlist").GetProperty("enabled").GetBoolean());
        Assert.Empty(configuration.RootElement.GetProperty("allowlist").GetProperty("logins").EnumerateArray());
    }

    // --- idempotent resync ---

    [Fact]
    public async Task Resync_UnchangedConfig_NoNewVersionNorDuplicates()
    {
        await postgres.ResetAsync();
        var config = Parse();
        await using var db = postgres.NewDbContext();
        var sync = Sync(config, _lastRoot, "abc123");
        await new ConfigSyncService(db).SyncAsync(sync);
        var firstSync = await db.AgentProfileVersions.AsNoTracking().SingleAsync();

        var second = await new ConfigSyncService(db).SyncAsync(sync);
        Assert.Equal(0, second.VersionsAdded);
        Assert.Equal(0, second.CursorsCreated);
        Assert.Equal(0, second.RepositoriesCreated);
        Assert.Equal(1, await db.AgentProfileVersions.AsNoTracking().CountAsync());
        Assert.Equal(1, await db.AgentProfileRoutingRules.AsNoTracking().CountAsync());
        Assert.Equal(7, await db.SyncCursors.AsNoTracking().CountAsync());
        Assert.Equal(firstSync.Id, (await db.AgentProfileVersions.AsNoTracking().SingleAsync()).Id);
    }

    [Fact]
    public async Task Resync_ChangedConfig_NewVersionAndRuleRepoint()
    {
        await postgres.ResetAsync();
        var config = Parse();
        await using var db = postgres.NewDbContext();
        var sync = Sync(config, _lastRoot, "abc123");
        await new ConfigSyncService(db).SyncAsync(sync);
        var v1 = await db.AgentProfileVersions.AsNoTracking().SingleAsync();

        var changedConfig = Parse(plannerYaml: ConfigSamples.PlannerProfile.Replace("max_context_tokens: 200000", "max_context_tokens: 250000"));
        var changed = Sync(changedConfig, _lastRoot, "abc123");
        var result = await new ConfigSyncService(db).SyncAsync(changed);

        Assert.Equal(1, result.VersionsAdded);
        var versions = await db.AgentProfileVersions.AsNoTracking().OrderBy(v => v.Version).ToListAsync();
        Assert.Equal(2, versions.Count);
        Assert.Equal(ProfileVersionStates.Draft, versions[1].State);
        Assert.Equal(2, versions[1].Version);
        Assert.NotEqual(v1.ConfigHash, versions[1].ConfigHash);

        var rule = await db.AgentProfileRoutingRules.AsNoTracking().SingleAsync();
        Assert.Equal(versions[1].Id, rule.AgentProfileVersionId);
    }

    // --- resync semantics (owner ruling Q5) ---

    [Fact]
    public async Task RepoRemovedFromYaml_Disabled_AndReadded_Reenabled()
    {
        await postgres.ResetAsync();
        var config = Parse();
        await using var db = postgres.NewDbContext();
        await new ConfigSyncService(db).SyncAsync(Sync(config, _lastRoot));

        var withoutRepo = Parse(repositoriesYaml: "repositories: []");
        var removed = await new ConfigSyncService(db).SyncAsync(Sync(withoutRepo, _lastRoot));
        Assert.Equal(1, removed.RepositoriesDisabled);
        Assert.False((await db.Repositories.AsNoTracking().SingleAsync()).IsEnabled);

        var backConfig = Parse();
        var readded = await new ConfigSyncService(db).SyncAsync(Sync(backConfig, _lastRoot));
        Assert.Equal(1, readded.RepositoriesEnabled);
        Assert.True((await db.Repositories.AsNoTracking().SingleAsync()).IsEnabled);
    }

    [Fact]
    public async Task RoutingRuleRemovedFromYaml_Deleted()
    {
        await postgres.ResetAsync();
        var config = Parse();
        await using var db = postgres.NewDbContext();
        await new ConfigSyncService(db).SyncAsync(Sync(config, _lastRoot));

        var withoutRules = Parse(routingYaml: "rules: []");
        var result = await new ConfigSyncService(db).SyncAsync(Sync(withoutRules, _lastRoot));
        Assert.Equal(1, result.RoutingRulesDeleted);
        Assert.Empty(await db.AgentProfileRoutingRules.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ProfileRemovedFromConfig_Disabled()
    {
        await postgres.ResetAsync();
        var config = Parse();
        await using var db = postgres.NewDbContext();
        await new ConfigSyncService(db).SyncAsync(Sync(config, _lastRoot));

        var withoutProfilesRoot = Directory.CreateTempSubdirectory("sync").FullName;
        ConfigSamples.WriteSampleTree(withoutProfilesRoot, repositories: OneRepo);
        Directory.Delete(Path.Combine(withoutProfilesRoot, "agents"), recursive: true);
        Directory.CreateDirectory(Path.Combine(withoutProfilesRoot, "agents"));
        var configWithoutProfile = ConfigParser.Parse(withoutProfilesRoot);
        var result = await new ConfigSyncService(db).SyncAsync(Sync(configWithoutProfile, withoutProfilesRoot));

        Assert.Equal(1, result.ProfilesDisabled);
        Assert.False((await db.AgentProfiles.AsNoTracking().SingleAsync()).IsEnabled);
    }

    // --- transaction integrity ---

    [Fact]
    public async Task DuplicateRepositoryIds_RollbackLeavesNoRows()
    {
        await postgres.ResetAsync();
        var config = Parse(repositoriesYaml: """
            repositories:
              - owner: org-one
                name: repo-one
              - owner: org-two
                name: repo-two
            """);
        await using var db = postgres.NewDbContext();

        var sync = Sync(config, _lastRoot);
        var forcedCollisions = new Dictionary<string, GitHubRepoInfo>(sync.Repositories)
        {
            ["org-two/repo-two"] = new(sync.Repositories["org-one/repo-one"].Id, "org-two/repo-two", "main"),
        };
        var collisionSync = sync with { Repositories = forcedCollisions };

        await Assert.ThrowsAnyAsync<Exception>(() => new ConfigSyncService(db).SyncAsync(collisionSync));
        Assert.Equal(0, await db.Repositories.AsNoTracking().CountAsync());
        Assert.Equal(0, await db.SyncCursors.AsNoTracking().CountAsync());
        Assert.Equal(0, await db.AgentProfiles.AsNoTracking().CountAsync());
        Assert.Equal(0, await db.AgentProfileVersions.AsNoTracking().CountAsync());
        Assert.Equal(0, await db.AgentProfileRoutingRules.AsNoTracking().CountAsync());
    }
}
