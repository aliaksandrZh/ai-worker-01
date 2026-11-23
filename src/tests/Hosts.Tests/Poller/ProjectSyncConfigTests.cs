using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Config;
using PiAgent.Poller;
using Xunit;

namespace PiAgent.Hosts.Tests.Poller;

/// <summary>projects.yaml startup semantics (PollerConfig precedent): strict plane reading —
/// valid boards parsed, an empty list is legal, missing/malformed files and invalid entries are
/// diagnosable startup failures.</summary>
public class ProjectSyncConfigTests
{
    private static string ConfigDir(string? yaml)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"projects-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        if (yaml is not null)
            File.WriteAllText(Path.Combine(dir, "projects.yaml"), yaml);
        return dir;
    }

    [Fact]
    public void Boards_ParsedFromYaml()
    {
        var dir = ConfigDir("""
            projects:
              - owner: aliaksandrZh
                number: 6
              - owner: someone-else
                number: 42
            """);
        try
        {
            var options = ProjectSyncConfig.Read(dir);
            Assert.Equal(2, options.Projects.Count);
            Assert.Equal(new ProjectRef("aliaksandrZh", 6), options.Projects[0]);
            Assert.Equal(new ProjectRef("someone-else", 42), options.Projects[1]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EmptyList_IsLegal_NoProjectPolling()
    {
        var dir = ConfigDir("projects: []\n");
        try
        {
            Assert.Empty(ProjectSyncConfig.Read(dir).Projects);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MissingFile_IsStartupFailure()
    {
        var dir = ConfigDir(null);
        try
        {
            var exception = Assert.Throws<ConfigException>(() => ProjectSyncConfig.Read(dir));
            Assert.Contains("projects.yaml", exception.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MalformedYaml_IsStartupFailure()
    {
        var dir = ConfigDir("projects: [ { owner: x, number: ");
        try
        {
            Assert.Throws<ConfigException>(() => ProjectSyncConfig.Read(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("projects:\n  - number: 6\n")]
    [InlineData("projects:\n  - owner: alice\n")]
    [InlineData("projects:\n  - owner: alice\n    number: 0\n")]
    [InlineData("projects:\n  - owner: alice\n    number: -3\n")]
    public void InvalidEntries_AreStartupFailures(string yaml)
    {
        var dir = ConfigDir(yaml);
        try
        {
            Assert.Throws<ConfigException>(() => ProjectSyncConfig.Read(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
