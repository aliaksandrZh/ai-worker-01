using PiAgent.Platform.Config;
using PiAgent.RuntimeApi.Provisioning;
using PiAgent.RuntimeApi.Api;
using PiAgent.RuntimeApi.Runs;
using YamlDotNet.Serialization;

namespace PiAgent.RuntimeApi.Runs;

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
            // Orchestrator's strict ConfigParser validates the whole system.yaml document). The knob
            // grammar is the shared SystemYaml duration (integer s/m/h, positive) — the former local
            // lenient copy accepted decimals and negatives the Orchestrator's schema rejects.
            var dto = new DeserializerBuilder().IgnoreUnmatchedProperties().Build()
                .Deserialize<SystemDto>(File.ReadAllText(path)) ?? new();
            return new RuntimeLimits
            {
                StartupTimeout = SystemYaml.Duration("startup_timeout", dto.StartupTimeout, TimeSpan.FromMinutes(10)),
                IdleTimeout = SystemYaml.Duration("idle_timeout", dto.IdleTimeout, TimeSpan.FromMinutes(60)),
                MaxRunDuration = SystemYaml.Duration("max_run_duration", dto.MaxRunDuration, TimeSpan.FromHours(4)),
                HeartbeatInterval = SystemYaml.Duration("heartbeat_interval", dto.HeartbeatInterval, TimeSpan.FromSeconds(30)),
                OrphanGracePeriod = SystemYaml.Duration("orphan_grace_period", dto.OrphanGracePeriod, TimeSpan.FromHours(24)),
                OrphanQuarantinePeriod = SystemYaml.Duration("orphan_quarantine_period", dto.OrphanQuarantinePeriod, TimeSpan.FromHours(24)),
            };
        }
        catch (Exception exception) when (exception is YamlDotNet.Core.YamlException or IOException)
        {
            throw new InvalidOperationException($"system.yaml: cannot read the runtime limits ({exception.Message})");
        }
        // a malformed/negative knob value surfaces as the shared ConfigException (naming the key) —
        // deliberately not wrapped: it is exactly the failure the Orchestrator's strict schema
        // validation rejects, so both hosts now agree on what makes system.yaml invalid.
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
