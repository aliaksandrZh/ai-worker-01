using System.Text.Json;
using PiAgent.Platform.Config;

namespace PiAgent.Orchestration;

/// <summary>
/// The config-plane repositories.json (deploy/config, the sync's source of registered
/// repositories): the Orchestrator reads only the optional per-entry default_branch —
/// the PR base branch a RunInput points at (docs/run-worker.md "Run input (pointers v2)").
/// The Orchestrator itself carries no GitHub knowledge; the branch table is config.
/// </summary>
public static class RepositoriesConfig
{
    public static IReadOnlyDictionary<string, string> ReadDefaultBranches(string configDir)
    {
        var root = ConfigJson.Read<RepositoriesRoot>(Path.Combine(configDir, "repositories.json"));
        var branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in root?.Repositories ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Owner) || string.IsNullOrWhiteSpace(entry.Name))
                continue;
            if (!string.IsNullOrWhiteSpace(entry.DefaultBranch))
                branches[$"{entry.Owner}/{entry.Name}"] = entry.DefaultBranch;
        }

        return branches;
    }

    private sealed class RepositoriesRoot
    {
        public List<RepoEntry> Repositories { get; init; } = [];
    }

    private sealed class RepoEntry
    {
        public string Owner { get; init; } = "";
        public string Name { get; init; } = "";
        public string? DefaultBranch { get; init; }
    }
}
