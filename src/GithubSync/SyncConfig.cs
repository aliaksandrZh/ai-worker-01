using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

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
        var repositoriesRoot = ReadFile(Path.Combine(configDir, "repositories.yaml"));
        var systemRoot = ReadFile(Path.Combine(configDir, "system.yaml"));

        var bot = "";
        if (systemRoot?["github"] is IDictionary<object, object> gh
            && gh.TryGetValue("bot_login", out var login))
            bot = login.ToString() ?? "";

        var repos = new List<RepoConfig>();
        if (repositoriesRoot?["repositories"] is IEnumerable<object> list)
        {
            foreach (var entry in list)
            {
                if (entry is IDictionary<object, object> map
                    && map.TryGetValue("owner", out var owner)
                    && map.TryGetValue("name", out var name))
                {
                    var enabled = !map.TryGetValue("enabled", out var flag) || flag?.ToString() != "false";
                    if (enabled)
                        repos.Add(new RepoConfig(owner.ToString()!, name.ToString()!));
                }
            }
        }

        return new SyncConfig { Repositories = [.. repos], BotLogin = bot };

        static Dictionary<object, object>? ReadFile(string path)
        {
            if (!File.Exists(path))
                return null;
            var yaml = File.ReadAllText(path);
            return new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build()
                .Deserialize<Dictionary<object, object>>(yaml);
        }
    }
}
