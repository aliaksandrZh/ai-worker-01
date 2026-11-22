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

    // Shared Runtime Bridge (agents/runtime-bridge/): merged into the pinned snapshot under the
    // runtime-bridge/ prefix; fail-closed on per-profile copies, version drift and a missing source.
    private string MakeBridgeSource(string version = "1.0.0") // matches ConfigSamples.PlannerProfile's bridge.version
    {
        var bridgeDirectory = Path.Combine(_root, "bridge-src");
        Directory.CreateDirectory(bridgeDirectory);
        File.WriteAllText(Path.Combine(bridgeDirectory, "bridge.json"), $"{{\"version\":\"{version}\"}}");
        File.WriteAllText(Path.Combine(bridgeDirectory, "bridge.mjs"), "// shared bridge");
        File.WriteAllText(Path.Combine(bridgeDirectory, "rpc.mjs"), "// shared rpc");
        File.WriteAllText(Path.Combine(bridgeDirectory, "runtime-bridge.test.mjs"), "// test — never baked");
        return bridgeDirectory;
    }

    [Fact]
    public void SharedBridge_Merged_UnderRuntimeBridgePrefix_AndVersionChecked()
    {
        var bridgeDirectory = MakeBridgeSource();
        var snapshot = ConfigSnapshotBuilder.Build(Build(), Path.Combine(_root, "planner"), null, bridgeDirectory);
        using var json = BuildJsonFrom(snapshot);

        var contents = json.RootElement.GetProperty("file_contents");
        Assert.Equal("// shared bridge", contents.GetProperty("runtime-bridge/bridge.mjs").GetString());
        Assert.Equal("// shared rpc", contents.GetProperty("runtime-bridge/rpc.mjs").GetString());
        // the shared test suite runs against the source, never bakes into a profile image
        Assert.False(contents.TryGetProperty("runtime-bridge/runtime-bridge.test.mjs", out _));
    }

    [Fact]
    public void SharedBridge_ProfileCopy_FailsClosed()
    {
        var bridgeDirectory = MakeBridgeSource();
        Directory.CreateDirectory(Path.Combine(_root, "planner", "runtime-bridge"));
        File.WriteAllText(Path.Combine(_root, "planner", "runtime-bridge", "bridge.mjs"), "// per-profile copy");
        var exception = Assert.Throws<ConfigException>(
            () => ConfigSnapshotBuilder.Build(Build(), Path.Combine(_root, "planner"), null, bridgeDirectory));
        Assert.Contains("runtime-bridge/bridge.mjs", exception.Message);
        Assert.Contains("per-profile copies are not allowed", exception.Message);
    }

    [Fact]
    public void SharedBridge_VersionDrift_FailsClosed()
    {
        var bridgeDirectory = MakeBridgeSource(version: "9.9.9");
        var profileDirectory = Path.Combine(_root, "planner");
        Directory.CreateDirectory(profileDirectory);
        File.WriteAllText(Path.Combine(profileDirectory, "profile.yaml"), "max_context_tokens: 100\n");
        var profile = new ProfileConfig { Name = "planner", MaxContextTokens = 200000, BridgeVersion = "1.0.0" };
        var exception = Assert.Throws<ConfigException>(
            () => ConfigSnapshotBuilder.Build(profile, profileDirectory, null, bridgeDirectory));
        Assert.Contains("bridge.version '1.0.0'", exception.Message);
        Assert.Contains("'9.9.9'", exception.Message);
    }

    [Fact]
    public void SharedBridge_MissingSourceDirectory_FailsClosed()
    {
        var exception = Assert.Throws<ConfigException>(
            () => ConfigSnapshotBuilder.Build(Build(), Path.Combine(_root, "planner"), null, Path.Combine(_root, "no-such-bridge")));
        Assert.Contains("shared runtime-bridge source directory", exception.Message);
    }

    [Fact]
    public void SharedBridge_ProfileWithoutBridgeBlock_StillMerges()
    {
        // bridge.version is optional (ConfigValidator only requires it when a bridge block exists);
        // the merge itself applies to every profile that bakes the image.
        var bridgeDirectory = MakeBridgeSource();
        var profileDirectory = Path.Combine(_root, "plain");
        Directory.CreateDirectory(profileDirectory);
        File.WriteAllText(Path.Combine(profileDirectory, "profile.yaml"), "max_context_tokens: 100\n");
        var snapshot = ConfigSnapshotBuilder.Build(new ProfileConfig { Name = "plain", MaxContextTokens = 100 }, profileDirectory, null, bridgeDirectory);
        using var json = BuildJsonFrom(snapshot);
        Assert.True(json.RootElement.GetProperty("file_contents").TryGetProperty("runtime-bridge/bridge.mjs", out _));
    }
}
