using PiAgent.OutboxWorker.GitHub;
using PiAgent.BuildWorker.Docker;
using PiAgent.BuildWorker.Builds;
using PiAgent.BuildWorker.Api;
using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using PiAgent.BuildWorker;
using Xunit;

namespace PiAgent.Hosts.Tests.BuildWorker;

/// <summary>
/// The pinned-build-context mechanics: the ustar writer is readable by the BCL tar reader and yields
/// deterministic bytes; the context composer materializes hash-verified files from the pinned snapshot
/// and rejects anything that would break the version identity (missing Dockerfile/profile.yaml,
/// hash mismatch, unsafe paths, list/content disagreement).
/// </summary>
public class BuildTarAndContextTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pi-build-worker-tar-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Tar_IsReadableUstar_WithExactEntryNamesTypesAndContent()
    {
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(Path.Combine(_directory, "runtime-bridge"));
        File.WriteAllText(Path.Combine(_directory, "Dockerfile"), "FROM ok\n");
        File.WriteAllText(Path.Combine(_directory, "profile.yaml"), "description: x\n");
        File.WriteAllText(Path.Combine(_directory, "runtime-bridge", "bridge.mjs"), "const x = 42;\n");

        var tar = TarBuilder.Create(_directory);

        using var stream = new MemoryStream(tar);
        using var reader = new TarReader(stream);
        var entries = new List<(string Name, TarEntryType Type, string? Content)>();
        while (reader.GetNextEntry() is { } entry)
        {
            string? content = null;
            if (entry.DataStream is not null)
            {
                using var memory = new MemoryStream();
                entry.DataStream.CopyTo(memory);
                content = Encoding.UTF8.GetString(memory.ToArray());
            }
            entries.Add((entry.Name, entry.EntryType, content));
        }
        var names = entries.Select(entry => entry.Name.TrimEnd('/')).ToList();
        // the writer emits parent directories first, then the sorted files
        Assert.Equal(new List<string> { "runtime-bridge", "Dockerfile", "profile.yaml", "runtime-bridge/bridge.mjs" }, names);
        Assert.Single(entries, entry => entry.Type == TarEntryType.Directory);
        var dockerfile = entries.Single(entry => entry.Name.TrimEnd('/') == "Dockerfile");
        Assert.Equal(TarEntryType.RegularFile, dockerfile.Type);
        Assert.Equal("FROM ok\n", dockerfile.Content);
        var bridge = entries.Single(entry => entry.Name.TrimEnd('/') == "runtime-bridge/bridge.mjs");
        Assert.Equal("const x = 42;\n", bridge.Content);
    }

    [Fact]
    public void Tar_IsDeterministicForTheSameContext()
    {
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(Path.Combine(_directory, "prompts"));
        File.WriteAllText(Path.Combine(_directory, "Dockerfile"), "FROM ok\n");
        File.WriteAllText(Path.Combine(_directory, "prompts", "p.md"), "prompt\n"); // later mtime by seconds — bytes must not change

        Thread.Sleep(1100); // ensure the filesystem mtime would move if the tar were not pinned
        var first = TarBuilder.Create(_directory);
        var second = TarBuilder.Create(_directory);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Composer_MaterializesThePinnedFiles()
    {
        var contextDirectory = Path.Combine(_directory, "context");
        var snapshot = BuildSnapshots.Valid("FROM ok\n", "Hello from the pinned context");

        BuildContextComposer.Compose(contextDirectory, snapshot);

        Assert.Equal("FROM ok\n", File.ReadAllText(Path.Combine(contextDirectory, "Dockerfile")));
        Assert.Equal("Hello from the pinned context", File.ReadAllText(Path.Combine(contextDirectory, "prompts/planner.md")));
        Assert.True(File.Exists(Path.Combine(contextDirectory, "profile.yaml")));
    }

    [Fact]
    public void Composer_RejectsUnreadableSnapshots()
    {
        Assert.Throws<BuildContextException>(() => BuildContextComposer.Compose(Path.Combine(_directory, "a"), "not json"));
        Assert.Throws<BuildContextException>(() => BuildContextComposer.Compose(Path.Combine(_directory, "b"), """{"files":[],"file_contents":{}}"""));
        // content present but absent from the hashed file list = snapshot corruption
        var orphan = """{"files":[{"path":"profile.yaml","sha256":"%SHA%"}],"file_contents":{"profile.yaml":"y","Dockerfile":"FROM ok"}}"""
            .Replace("%SHA%", BuildSnapshots.Sha256("y")); // content in file_contents but not in the hashed list
        Assert.Throws<BuildContextException>(() => BuildContextComposer.Compose(Path.Combine(_directory, "c"), orphan));
    }

    [Fact]
    public void Composer_RejectsPathTraversal()
    {
        var snapshot = BuildSnapshots.BuildJson(new Dictionary<string, string>
        {
            ["Dockerfile"] = "FROM ok\n",
            ["../escape.yaml"] = "content",
        });
        var exception = Assert.Throws<BuildContextException>(
            () => BuildContextComposer.Compose(Path.Combine(_directory, "traversal"), snapshot));
        Assert.Contains("not a safe relative path", exception.Message);
        Assert.False(File.Exists(Path.Combine(_directory, "escape.yaml")), "the traversal must not write outside the context directory");
    }
}
