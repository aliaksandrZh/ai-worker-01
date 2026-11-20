using PiAgent.OutboxWorker;
using Xunit;

namespace PiAgent.Hosts.Tests.Outbox;

/// <summary>
/// Outbox Worker's single config-plane read (plan ruling 5): `outbox_retry_window` from
/// config/system.yaml. Missing key → 24 h default (mirrors the Orchestrator's knob default);
/// a missing system.ya&#8203;ml file or a malformed/non-positive value fails startup naming key/value.
/// </summary>
public class OutboxWorkerConfigTests
{
    private static string WriteConfig(string? yaml)
    {
        var root = Directory.CreateTempSubdirectory("outbox-config").FullName;
        if (yaml is not null)
            File.WriteAllText(Path.Combine(root, "system.yaml"), yaml);
        return root;
    }

    [Fact]
    public void ValidValue_Parsed()
    {
        var root = WriteConfig("github:\n  bot_login: jsxhuz\noutbox_retry_window: 24h\n");

        var options = OutboxWorkerConfig.Read(root);

        Assert.Equal(TimeSpan.FromHours(24), options.RetryWindow);
    }

    [Fact]
    public void ValidValue_Minutes_Parsed()
    {
        var root = WriteConfig("outbox_retry_window: 90m\n");

        var options = OutboxWorkerConfig.Read(root);

        Assert.Equal(TimeSpan.FromMinutes(90), options.RetryWindow);
    }

    [Fact]
    public void MissingKey_Default24h()
    {
        var root = WriteConfig("github:\n  bot_login: jsxhuz\npoll_interval: 60s\n");

        var options = OutboxWorkerConfig.Read(root);

        Assert.Equal(TimeSpan.FromHours(24), options.RetryWindow);
    }

    [Fact]
    public void MissingFile_Errors()
    {
        var root = Directory.CreateTempSubdirectory("outbox-config-empty").FullName;

        var exception = Assert.Throws<OutboxWorkerConfigException>(() => OutboxWorkerConfig.Read(root));

        Assert.Contains(root, exception.Message);
        Assert.Contains("system.yaml", exception.Message);
    }

    [Fact]
    public void MalformedValue_Errors()
    {
        var root = WriteConfig("outbox_retry_window: banana\n");

        var exception = Assert.Throws<OutboxWorkerConfigException>(() => OutboxWorkerConfig.Read(root));

        Assert.Contains("outbox_retry_window", exception.Message);
        Assert.Contains("banana", exception.Message);
    }

    [Fact]
    public void ZeroValue_Errors()
    {
        var root = WriteConfig("outbox_retry_window: 0s\n");

        var exception = Assert.Throws<OutboxWorkerConfigException>(() => OutboxWorkerConfig.Read(root));

        Assert.Contains("outbox_retry_window", exception.Message);
    }
}
