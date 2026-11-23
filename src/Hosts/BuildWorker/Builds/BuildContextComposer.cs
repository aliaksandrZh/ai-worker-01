using PiAgent.Platform.Contracts;
using PiAgent.BuildWorker.Docker;
using PiAgent.BuildWorker.Builds;
using PiAgent.BuildWorker.Api;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiAgent.BuildWorker.Builds;

/// <summary>
/// Materializes the pinned build context from the dispatch payload's config_snapshot (04 §6). The
/// snapshot (AgentProfileVersion.config_snapshot, built by the Config Loader per decision 54) contains
/// the profile's file list with per-file sha256 AND the file contents — the version is rebuildable
/// from the dispatch alone, so the Worker never reads a possibly-moved Git tree: the built image is
/// guaranteed to match the pinned version even if `config/agents/<profile>` changed in between.
/// Each materialized file is hash-verified before it lands in the context.
/// </summary>
public static class BuildContextComposer
{
    private sealed record SnapshotShape(
        [property: JsonPropertyName("files")] List<SnapshotFile>? Files,
        [property: JsonPropertyName("file_contents")] SortedDictionary<string, string>? FileContents);

    private sealed record SnapshotFile([property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("sha256")] string Sha256);

    /// <summary>Writes the pinned files under <paramref name="contextDirectory"/>; throws
    /// <see cref="BuildContextException"/> on anything that would make the docker build meaningless.</summary>
    public static void Compose(string contextDirectory, string configSnapshotJson)
    {
        SnapshotShape? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<SnapshotShape>(configSnapshotJson, BuildJson.Default);
        }
        catch (JsonException exception)
        {
            throw new BuildContextException($"the pinned config snapshot is not readable JSON: {exception.Message}");
        }
        var files = snapshot?.Files;
        var contents = snapshot?.FileContents;
        if (files is null || files.Count == 0 || contents is null || contents.Count == 0)
            throw new BuildContextException("the pinned config snapshot carries no files — nothing to build");

        HashSet<string> recordedPaths = files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var path in contents.Keys)
            if (!recordedPaths.Contains(path))
                throw new BuildContextException($"pinned file '{path}' is present as content but not in the pinned file list (hashes are part of the version identity)");
        var hashes = files.ToDictionary(file => file.Path, file => file.Sha256, StringComparer.Ordinal);

        // path safety first: reject anything non-relative before any presence/hash/materialization work
        foreach (var relative in contents.Keys)
            AssertSafePath(relative);

        if (!contents.ContainsKey("Dockerfile"))
            throw new BuildContextException("the pinned file list has no Dockerfile — the version predates the profile Dockerfile (any configuration change creates a new version)");
        if (!contents.ContainsKey("profile.yaml"))
            throw new BuildContextException("the pinned file list has no profile.yaml — profile directories always carry one");

        if (Directory.Exists(contextDirectory))
            Directory.Delete(contextDirectory, recursive: true);
        Directory.CreateDirectory(contextDirectory);

        foreach (var (relative, content) in contents)
        {
            if (SafeHash(content) != hashes.GetValueOrDefault(relative, string.Empty))
                throw new BuildContextException($"pinned hash mismatch for '{relative}' — the snapshot content and its pinned sha256 disagree (version identity broken)");
            var target = Path.Combine(contextDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }
    }

    /// <summary>The snapshot paths come from the Config Loader and are relative already; the worker
    /// trusts nothing anyway — reject absolute paths, traversal, and drive-relative nonsense.</summary>
    private static void AssertSafePath(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(part => part is ".." or "."))
            throw new BuildContextException($"pinned file path '{relative}' is not a safe relative path");
    }

    private static string SafeHash(string content)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
