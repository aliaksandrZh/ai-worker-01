using PiAgent.Platform.Config;

namespace PiAgent.Poller;

/// <summary>Polling knobs from config/system.yaml: `poll_interval` 60 s per
/// repository, `poll_overlap` 5-minute window for timestamp queries, `per_page` 100. Missing key →
/// default; missing file or malformed/non-positive value → startup failure. Duration strings parse
/// via the shared SystemYaml mechanics (formerly a per-host local copy of the duration regex).</summary>
public record PollerOptions(TimeSpan Interval, TimeSpan Overlap, int PerPage)
{
    public static readonly PollerOptions Defaults = new(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5), 100);
}

public static class PollerConfig
{
    public static PollerOptions Read(string configRoot)
    {
        var dto = SystemYaml.Deserialize<YamlDto>(SystemYaml.Read(configRoot));
        return new PollerOptions(
            SystemYaml.Duration("poll_interval", dto.PollInterval, PollerOptions.Defaults.Interval),
            SystemYaml.Duration("poll_overlap", dto.PollOverlap, PollerOptions.Defaults.Overlap),
            SystemYaml.PositiveInt("per_page", dto.PerPage, PollerOptions.Defaults.PerPage));
    }

    private sealed class YamlDto
    {
        [YamlDotNet.Serialization.YamlMember(Alias = "poll_interval")] public string? PollInterval { get; set; }
        [YamlDotNet.Serialization.YamlMember(Alias = "poll_overlap")] public string? PollOverlap { get; set; }
        [YamlDotNet.Serialization.YamlMember(Alias = "per_page")] public string? PerPage { get; set; }
    }
}
