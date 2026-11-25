using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PiAgent.Orchestration;

/// <summary>
/// The config-plane repositories.yaml (deploy/config, the sync's source of registered
/// repositories): the Orchestrator reads only the optional per-entry default_branch —
/// the PR base branch a RunInput points at (docs/run-worker.md "Run input (pointers v2)").
/// The Orchestrator itself carries no GitHub knowledge; the branch table is config.
/// </summary>
public static class RepositoriesConfig
{
    public static IReadOnlyDictionary<string, string> ReadDefaultBranches(string configDir)
    {
        var path = Path.Combine(configDir, "repositories.yaml");
        if (!File.Exists(path))
            return new Dictionary<string, string>();

        var root = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build()
            .Deserialize<Dictionary<object, object>>(File.ReadAllText(path));

        var branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root?["repositories"] is not IEnumerable<object> list)
            return branches;

        foreach (var entry in list.OfType<IDictionary<object, object>>())
        {
            if (!entry.TryGetValue("owner", out var owner) || !entry.TryGetValue("name", out var name))
                continue;
            if (entry.TryGetValue("default_branch", out var branch) && branch?.ToString() is { Length: > 0 } value)
                branches[$"{owner}/{name}"] = value;
        }

        return branches;
    }
}
