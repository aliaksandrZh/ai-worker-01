using PiAgent.Platform.Config;

namespace PiAgent.GithubSync;

/// <summary>Sync configuration (config plane): repositories + the bot identity.</summary>
public sealed class SyncConfig
{
    public IReadOnlyList<RepoConfig> Repositories { get; init; } = [];
    public string BotLogin { get; init; } = "";
    public int PollIntervalSeconds { get; init; } = 60;

    public sealed record RepoConfig(string Owner, string Name);

    public static SyncConfig Read(string configDir)
    {
        var system = ConfigJson.Read<SystemRoot>(Path.Combine(configDir, "system.json"));
        var repositories = ConfigJson.Read<RepositoriesRoot>(Path.Combine(configDir, "repositories.json"));

        var repos = new List<RepoConfig>();
        foreach (var entry in repositories?.Repositories ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Owner) || string.IsNullOrWhiteSpace(entry.Name))
                continue;
            if (entry.Enabled ?? true)
                repos.Add(new RepoConfig(entry.Owner, entry.Name));
        }

        return new SyncConfig { Repositories = [.. repos], BotLogin = system?.Github?.BotLogin ?? "" };
    }

    private sealed class SystemRoot
    {
        public GithubSection? Github { get; init; }
    }

    private sealed class GithubSection
    {
        public string BotLogin { get; init; } = "";
    }

    private sealed class RepositoriesRoot
    {
        public List<RepoEntry> Repositories { get; init; } = [];
    }

    private sealed class RepoEntry
    {
        public string Owner { get; init; } = "";
        public string Name { get; init; } = "";
        public bool? Enabled { get; init; }
    }
}
