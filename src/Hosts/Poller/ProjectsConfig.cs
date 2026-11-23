using YamlDotNet.Serialization;
using PiAgent.Platform.Config;

namespace PiAgent.Poller;

/// <summary>
/// One configured Projects v2 board (projects.yaml): the account login owning the project —
/// user-owned boards are addressed as `/users/{owner}/projectsV2/{number}` — and the project
/// number from the board URL (`.../users/{login}/projects/{number}/views/...`).
/// </summary>
public sealed record ProjectRef(string Owner, int Number);

/// <summary>The projects pass config, read ONCE at startup (same stance as PollerConfig):
/// strict plane semantics — a missing or malformed projects.yaml is a diagnosable startup
/// failure; an empty `projects:` list simply means no project polling.</summary>
public sealed record ProjectSyncOptions(IReadOnlyList<ProjectRef> Projects)
{
    public static readonly ProjectSyncOptions Empty = new([]);
}

public static class ProjectSyncConfig
{
    public static ProjectSyncOptions Read(string configRoot)
    {
        var path = Path.Combine(configRoot, "projects.yaml");
        if (!File.Exists(path))
            throw new ConfigException($"configuration error: the configuration file '{path}' does not exist");

        YamlDto dto;
        try
        {
            dto = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<YamlDto>(File.ReadAllText(path)) ?? new();
        }
        catch (YamlDotNet.Core.YamlException exception)
        {
            throw new ConfigException($"configuration error: projects.yaml is not valid YAML ({exception.Message})");
        }

        var projects = new List<ProjectRef>();
        foreach (var entry in dto.Projects ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Owner))
                throw new ConfigException("configuration error: projects.yaml entry without 'owner'");
            if (entry.Number is not { } number || number <= 0)
                throw new ConfigException(
                    $"configuration error: projects.yaml entry '{entry.Owner}' must have a positive 'number' (the number from the board URL)");
            projects.Add(new ProjectRef(entry.Owner.Trim(), number));
        }
        return new ProjectSyncOptions(projects);
    }

    private sealed class YamlDto
    {
        [YamlDotNet.Serialization.YamlMember(Alias = "projects")] public List<ProjectDto>? Projects { get; set; }
    }

    private sealed class ProjectDto
    {
        [YamlDotNet.Serialization.YamlMember(Alias = "owner")] public string? Owner { get; set; }
        [YamlDotNet.Serialization.YamlMember(Alias = "number")] public int? Number { get; set; }
    }
}
