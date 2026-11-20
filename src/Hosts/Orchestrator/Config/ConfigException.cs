namespace PiAgent.Orchestrator.Config;

/// <summary>Configuration-plane error; message always names the config file (and YAML path) at fault.</summary>
public sealed class ConfigException(string message) : Exception(message);
