namespace PiAgent.Platform.Config;

/// <summary>
/// Startup configuration error shared by every host: unset/missing/malformed configuration must
/// abort host startup with a diagnosable message naming the env var, file path, or knob — never the
/// secret content (§14, decision 76). Replaces the former per-host one-liners
/// (PollerConfigException / OutboxWorkerConfigException); no host catches its own config errors,
/// so the shared type is behaviorally identical.
/// </summary>
public sealed class ConfigException(string message) : Exception(message);
