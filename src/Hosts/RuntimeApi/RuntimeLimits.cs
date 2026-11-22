using System.Globalization;
using YamlDotNet.Serialization;

namespace PiAgent.RuntimeApi;

/// <summary>
/// The §16 operational knobs the Runtime API's reconciliation enforces (ticket 13): `startup_timeout`,
/// `idle_timeout`, `max_run_duration`, `heartbeat_interval` and the decision-77 orphan windows, read
/// from `config/system.yaml` (the configuration plane; §16: profile versions may lower but never
/// exceed these). All values are positive durations; the §16 spec defaults apply when a key is absent.
/// </summary>
public sealed class RuntimeLimits
{
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(60);
    public TimeSpan MaxRunDuration { get; init; } = TimeSpan.FromHours(4);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan OrphanGracePeriod { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan OrphanQuarantinePeriod { get; init; } = TimeSpan.FromHours(24);

    public static RuntimeLimits Load(string? configRoot)
    {
        const string name = "system.yaml";
        var path = string.IsNullOrEmpty(configRoot) ? null : Path.Combine(configRoot, name);
        if (path is null || !File.Exists(path))
            return new RuntimeLimits(); // the spec defaults (a missing file is the §16 default document)

        try
        {
            // a narrow reader of the §16 duration knobs only — unknown keys are ignored here (the
            // Orchestrator's strict ConfigParser validates the whole system.yaml document)
            var dto = new DeserializerBuilder().IgnoreUnmatchedProperties().Build()
                .Deserialize<SystemDto>(File.ReadAllText(path)) ?? new();
            return new RuntimeLimits
            {
                StartupTimeout = Parse("startup_timeout", dto.StartupTimeout, TimeSpan.FromMinutes(10)),
                IdleTimeout = Parse("idle_timeout", dto.IdleTimeout, TimeSpan.FromMinutes(60)),
                MaxRunDuration = Parse("max_run_duration", dto.MaxRunDuration, TimeSpan.FromHours(4)),
                HeartbeatInterval = Parse("heartbeat_interval", dto.HeartbeatInterval, TimeSpan.FromSeconds(30)),
                OrphanGracePeriod = Parse("orphan_grace_period", dto.OrphanGracePeriod, TimeSpan.FromHours(24)),
                OrphanQuarantinePeriod = Parse("orphan_quarantine_period", dto.OrphanQuarantinePeriod, TimeSpan.FromHours(24)),
            };
        }
        catch (Exception exception) when (exception is YamlDotNet.Core.YamlException or IOException)
        {
            throw new InvalidOperationException($"system.yaml: cannot read the runtime limits ({exception.Message})");
        }
    }

    private static TimeSpan Parse(string key, string? value, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        var text = value.Trim();
        // duration knob shape used across the plane: `10m`, `60s`, `4h`, `24h`, `30s`
        var unit = text[^1];
        var numberText = text[..^1];
        if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            throw new InvalidOperationException($"system.yaml: {key} '{value}' is not a duration (e.g. 10m, 60s, 4h)");
        return unit switch
        {
            's' => TimeSpan.FromSeconds(number),
            'm' => TimeSpan.FromMinutes(number),
            'h' => TimeSpan.FromHours(number),
            _ => throw new InvalidOperationException($"system.yaml: {key} '{value}' must end in s/m/h"),
        };
    }

    private sealed class SystemDto
    {
        [YamlDotNet.Serialization.YamlMember(Alias = "startup_timeout")] public string? StartupTimeout { get; set; }
        [YamlDotNet.Serialization.YamlMember(Alias = "idle_timeout")] public string? IdleTimeout { get; set; }
        [YamlDotNet.Serialization.YamlMember(Alias = "max_run_duration")] public string? MaxRunDuration { get; set; }
        [YamlDotNet.Serialization.YamlMember(Alias = "heartbeat_interval")] public string? HeartbeatInterval { get; set; }
        [YamlDotNet.Serialization.YamlMember(Alias = "orphan_grace_period")] public string? OrphanGracePeriod { get; set; }
        [YamlDotNet.Serialization.YamlMember(Alias = "orphan_quarantine_period")] public string? OrphanQuarantinePeriod { get; set; }
    }
}
