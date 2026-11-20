using System.Text.Json;
using PiAgent.Orchestrator.Config;
using Xunit;

namespace PiAgent.Hosts.Tests.Config;

/// <summary>
/// Immutable version pinning (02 §12, decision 54, owner rulings): the snapshot carries normalized
/// profile.yaml, per-file sha256 AND file contents (rebuildable without Git); config_hash is sha256
/// over the canonical snapshot JSON. source_commit_sha is optional traceability metadata.
/// </summary>
public class ConfigSnapshotTests : IDisposable
{
    private readonly string _root;

    public ConfigSnapshotTests() => _root = Directory.CreateTempSubdirectory("snap").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ProfileConfig Build(
        string? sourceCommitSha = null,
        string profileYaml = ConfigSamples.PlannerProfile,
        string name = "planner")
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "profile.yaml"), profileYaml);
        var profile = new ProfileConfig { Name = name, MaxContextTokens = 200000 };
        return profile;
    }

    private static JsonDocument BuildJsonFrom(ProfileSnapshot snapshot) => JsonDocument.Parse(snapshot.SnapshotJson);

    [Fact]
    public void Hash_Deterministic_AcrossRebuilds()
    {
        var profileYaml = ConfigSamples.PlannerProfile;
        var first = ConfigSnapshotBuilder.Build(Build(profileYaml: profileYaml), Path.Combine(_root, "planner"), "abc123");
        var second = ConfigSnapshotBuilder.Build(Build(profileYaml: profileYaml), Path.Combine(_root, "planner"), "abc123");
        Assert.Equal(first.ConfigHash, second.ConfigHash);
        Assert.Equal(first.SnapshotJson, second.SnapshotJson);
    }

    [Fact]
    public void Hash_Changes_WhenFileContentChanges()
    {
        var before = ConfigSnapshotBuilder.Build(Build(profileYaml: "max_context_tokens: 100\n"), Path.Combine(_root, "planner"), null);
        var after = ConfigSnapshotBuilder.Build(Build(profileYaml: "max_context_tokens: 120\n"), Path.Combine(_root, "planner"), null);
        Assert.NotEqual(before.ConfigHash, after.ConfigHash);
    }

    [Fact]
    public void Hash_Changes_WhenFileAdded()
    {
        var before = ConfigSnapshotBuilder.Build(Build(), Path.Combine(_root, "planner"), null);
        Directory.CreateDirectory(Path.Combine(_root, "planner", "prompts"));
        File.WriteAllText(Path.Combine(_root, "planner", "prompts", "plan.md"), "plan prompt");
        var after = ConfigSnapshotBuilder.Build(Build(), Path.Combine(_root, "planner"), null);
        Assert.NotEqual(before.ConfigHash, after.ConfigHash);
    }

    [Fact]
    public void Snapshot_ContainsFileEntriesWithSha256()
    {
        var snapshot = ConfigSnapshotBuilder.Build(Build(), Path.Combine(_root, "planner"), null);
        using var json = BuildJsonFrom(snapshot);
        var files = json.RootElement.GetProperty("files");
        var first = files[0];
        Assert.Equal("profile.yaml", first.GetProperty("path").GetString());
        Assert.Matches("^[0-9a-f]{64}$", first.GetProperty("sha256").GetString()!);
    }

    [Fact]
    public void Snapshot_ContainsFileContents()
    {
        Directory.CreateDirectory(Path.Combine(_root, "planner", "prompts"));
        File.WriteAllText(Path.Combine(_root, "planner", "prompts", "plan.md"), "write the plan");
        var snapshot = ConfigSnapshotBuilder.Build(Build(), Path.Combine(_root, "planner"), null);
        using var json = BuildJsonFrom(snapshot);
        Assert.Equal("write the plan", json.RootElement.GetProperty("file_contents").GetProperty("prompts/plan.md").GetString());
    }

    [Fact]
    public void Snapshot_NormalizedProfileYaml()
    {
        var snapshot = ConfigSnapshotBuilder.Build(Build(), Path.Combine(_root, "planner"), null);
        using var json = BuildJsonFrom(snapshot);
        Assert.Equal(200000, json.RootElement.GetProperty("profile_yaml").GetProperty("max_context_tokens").GetInt64());
    }

    [Fact]
    public void OversizeDirectory_FailsWithDiagnosableMessage()
    {
        var profileDirectory = Path.Combine(_root, "planner");
        Directory.CreateDirectory(profileDirectory);
        File.WriteAllText(Path.Combine(profileDirectory, "profile.yaml"), ConfigSamples.PlannerProfile);
        File.WriteAllText(Path.Combine(profileDirectory, "padding.bin"), new string('x', 1_100_000));
        var exception = Assert.Throws<ConfigException>(
            () => ConfigSnapshotBuilder.Build(Build(), profileDirectory, null));
        Assert.Contains("planner", exception.Message);
        Assert.Contains("1 MiB", exception.Message);
    }

    [Fact]
    public void SourceCommitSha_WhenProvided_Pinned_And_WhenAbsent_Null()
    {
        var withSha = BuildJsonFrom(ConfigSnapshotBuilder.Build(Build(), Path.Combine(_root, "planner"), "abc123"));
        Assert.Equal("abc123", withSha.RootElement.GetProperty("source_commit_sha").GetString());
        var withoutSha = BuildJsonFrom(ConfigSnapshotBuilder.Build(Build(), Path.Combine(_root, "planner"), null));
        Assert.Equal(JsonValueKind.Null, withoutSha.RootElement.GetProperty("source_commit_sha").ValueKind);
    }

    [Fact]
    public void Snapshot_CarriesProfileIdentityAndDirectory()
    {
        var snapshot = ConfigSnapshotBuilder.Build(Build(), Path.Combine(_root, "planner"), null);
        using var json = BuildJsonFrom(snapshot);
        Assert.Equal("planner", json.RootElement.GetProperty("profile").GetString());
        Assert.Equal("config/agents/planner", json.RootElement.GetProperty("source_directory").GetString());
    }
}
