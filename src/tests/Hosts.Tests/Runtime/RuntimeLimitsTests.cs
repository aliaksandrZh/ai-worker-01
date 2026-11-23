using PiAgent.RuntimeApi.Runs;
using Xunit;

namespace PiAgent.Hosts.Tests.Runtime;

/// <summary>
/// The Runtime API's knob reading: defaults when system.yaml is absent, and —
/// since the knob grammar unified on Platform's SystemYaml — the same strict integer/positive
/// duration grammar the Orchestrator's schema validation enforces (the former local lenient copy
/// accepted decimals and negatives the Orchestrator rejects).
/// </summary>
public class RuntimeLimitsTests
{
    private static string WriteConfig(string yaml)
    {
        var root = Directory.CreateTempSubdirectory("runtime-limits").FullName;
        File.WriteAllText(Path.Combine(root, "system.yaml"), yaml);
        return root;
    }

    [Fact]
    public void MissingFile_SpecDefaults()
    {
        // the Runtime API starts without a config dir: the default document applies
        var limits = RuntimeLimits.Load(null);
        Assert.Equal(TimeSpan.FromMinutes(10), limits.StartupTimeout);
        Assert.Equal(TimeSpan.FromMinutes(60), limits.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(4), limits.MaxRunDuration);
    }

    [Fact]
    public void ValidKnobs_Parsed()
    {
        var root = WriteConfig("startup_timeout: 20m\nidle_timeout: 90m\nmax_run_duration: 6h\n");
        var limits = RuntimeLimits.Load(root);
        Assert.Equal(TimeSpan.FromMinutes(20), limits.StartupTimeout);
        Assert.Equal(TimeSpan.FromMinutes(90), limits.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(6), limits.MaxRunDuration);
        Directory.Delete(root, true);
    }

    [Fact]
    public void MalformedDuration_ThrowsNamingKey()
    {
        var root = WriteConfig("idle_timeout: banana\n");
        var exception = Assert.Throws<PiAgent.Platform.Config.ConfigException>(() => RuntimeLimits.Load(root));
        Assert.Contains("idle_timeout", exception.Message);
        Directory.Delete(root, true);
    }

    [Fact]
    public void DecimalDuration_RejectedLikeTheOrchestratorRejects()
    {
        // the former local copy accepted 1.5h; the Orchestrator's schema never did
        var root = WriteConfig("idle_timeout: 1.5h\n");
        Assert.Throws<PiAgent.Platform.Config.ConfigException>(() => RuntimeLimits.Load(root));
        Directory.Delete(root, true);
    }

    [Fact]
    public void NegativeDuration_Rejected()
    {
        // the former local copy silently accepted -5m (no positivity check)
        var root = WriteConfig("idle_timeout: -5m\n");
        var exception = Assert.Throws<PiAgent.Platform.Config.ConfigException>(() => RuntimeLimits.Load(root));
        Assert.Contains("must be an integer", exception.Message);
        Directory.Delete(root, true);
    }
}
