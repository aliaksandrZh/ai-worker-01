namespace PiAgent.Poller;

/// <summary>Poller startup configuration errors — abort host startup with a diagnosable message
/// (OutboxWorkerConfigException precedent).</summary>
public sealed class PollerConfigException(string message) : Exception(message);
