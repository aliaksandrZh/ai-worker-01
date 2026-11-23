using PiAgent.OutboxWorker.GitHub;
using PiAgent.BuildWorker.Docker;
using PiAgent.BuildWorker.Builds;
using PiAgent.BuildWorker.Api;
using PiAgent.OutboxWorker;
using Xunit;

namespace PiAgent.Hosts.Tests.Outbox;

/// <summary>
/// Host-managed write-PAT reading (plan ruling 2 — same discipline as the Config Loader's ReadPat):
/// unset/missing/empty fail startup naming the env var or file path, never the token content.
/// </summary>
public class PatFileTests
{
    private const string EnvName = "PI_AGENT_GITHUB_WRITE_PAT_FILE";

    [Fact]
    public void Unset_ErrorsNamingEnvVar()
    {
        var exception = Assert.Throws<OutboxWorkerConfigException>(() => PatFile.Read(null, EnvName));
        Assert.Contains(EnvName, exception.Message);
    }

    [Fact]
    public void MissingFile_NamesPath()
    {
        var exception = Assert.Throws<OutboxWorkerConfigException>(
            () => PatFile.Read("/nonexistent/github-write-pat.txt", EnvName));
        Assert.Contains("/nonexistent/github-write-pat.txt", exception.Message);
    }

    [Fact]
    public void EmptyFile_ErrorsNeverHasContent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"outbox-pat-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "  \n");

        var exception = Assert.Throws<OutboxWorkerConfigException>(() => PatFile.Read(path, EnvName));

        Assert.Contains(path, exception.Message);
    }

    [Fact]
    public void ValidFile_ReturnsTrimmedToken()
    {
        var path = Path.Combine(Path.GetTempPath(), $"outbox-pat-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "  ghp_valid-token  \n");

        var token = PatFile.Read(path, EnvName);

        Assert.Equal("ghp_valid-token", token);
        File.Delete(path);
    }
}
