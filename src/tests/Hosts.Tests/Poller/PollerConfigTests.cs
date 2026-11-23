using PiAgent.Poller;
using Xunit;

namespace PiAgent.Hosts.Tests.Poller;

/// <summary>
/// Poller startup knobs from config/system.yaml (OutboxWorkerConfig precedent): defaults
/// (60s / 5m / 100), parsing, and startup failures — missing file, malformed duration,
/// non-positive per_page.
/// </summary>
public class PollerConfigTests
{
    private static string ConfigDir(Dictionary<string, string> entries)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"poller-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "system.yaml"),
            string.Join("\n", entries.Select(kv => $"{kv.Key}: {kv.Value}")) + "\n");
        return dir;
    }

    private static (PollerOptions Options, string Dir) Read(Dictionary<string, string>? entries = null)
    {
        var dir = ConfigDir(entries ?? new Dictionary<string, string> { ["github.bot_login"] = "bot" });
        return (PollerConfig.Read(dir), dir);
    }

    [Fact]
    public void Defaults_WhenKnobsAbsent()
    {
        var (options, dir) = Read();
        try
        {
            Assert.Equal(TimeSpan.FromSeconds(60), options.Interval);
            Assert.Equal(TimeSpan.FromMinutes(5), options.Overlap);
            Assert.Equal(100, options.PerPage);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Knobs_ParsedFromSystemYaml()
    {
        var (options, dir) = Read(new Dictionary<string, string>
        {
            ["github.bot_login"] = "bot",
            ["poll_interval"] = "90s",
            ["poll_overlap"] = "10m",
            ["per_page"] = "50",
        });
        try
        {
            Assert.Equal(TimeSpan.FromSeconds(90), options.Interval);
            Assert.Equal(TimeSpan.FromMinutes(10), options.Overlap);
            Assert.Equal(50, options.PerPage);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MissingSystemYaml_Throws()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"poller-config-{Guid.NewGuid():N}");
        var exception = Assert.Throws<PiAgent.Platform.Config.ConfigException>(() => PollerConfig.Read(missing));
        Assert.Contains("system.yaml", exception.Message);
    }

    [Fact]
    public void MalformedDuration_Throws()
    {
        var dir = ConfigDir(new Dictionary<string, string> { ["poll_interval"] = "banana" });
        try
        {
            Assert.Throws<PiAgent.Platform.Config.ConfigException>(() => PollerConfig.Read(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NonPositivePerPage_Throws()
    {
        Assert.Throws<PiAgent.Platform.Config.ConfigException>(() => Read(new Dictionary<string, string> { ["per_page"] = "0" }));
        Assert.Throws<PiAgent.Platform.Config.ConfigException>(() => Read(new Dictionary<string, string> { ["per_page"] = "-5" }));
    }
}
