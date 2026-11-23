using PiAgent.Platform.Config;

namespace PiAgent.OutboxWorker;

/// <summary>
/// The Outbox Worker reads exactly one knob directly from the configuration plane:
/// `outbox_retry_window` in deploy/config/system.yaml (default 24 h). Missing key →
/// default; missing file or a malformed/non-positive value → startup failure. Duration strings parse
/// via the shared SystemYaml mechanics (formerly a per-host local copy of the duration regex).
/// </summary>
public static class OutboxWorkerConfig
{
    public static OutboxWorkerOptions Read(string configRoot)
    {
        var dto = SystemYaml.Deserialize<YamlDto>(SystemYaml.Read(configRoot));
        return new OutboxWorkerOptions(
            SystemYaml.Duration("outbox_retry_window", dto.OutboxRetryWindow, OutboxWorkerOptions.DefaultRetryWindow));
    }

    private sealed class YamlDto
    {
        [YamlDotNet.Serialization.YamlMember(Alias = "outbox_retry_window")]
        public string? OutboxRetryWindow { get; set; }
    }
}
