using System.Text.Json;
using System.Text.Json.Serialization;
using PiAgent.Infrastructure.Persistence;

namespace PiAgent.Orchestrator.Commands;

/// <summary>
/// The resolved per-repository command policy (02 §21) with the ticket-02 defaults applied (all V1
/// commands, both target types). Extracted from CommentCommandProcessor (task 06) so the claim-time
/// authorization recheck (task 07) parses the identical policy shape.
/// </summary>
public sealed record RepoCommandConfig(
    IReadOnlyList<string> PermittedCommands,
    IReadOnlyList<string> PermittedWorkItemTypes,
    string? MinimumPermission)
{
    public static RepoCommandConfig Defaults() =>
        new(RepoCommandConfigParser.AllCommands(), [WorkItemTypes.Issue, WorkItemTypes.PullRequest], null);
}

public static class RepoCommandConfigParser
{
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The configuration plane syncs validated JSON; a data bug here fails open to the defaults
    /// for command lists (never silently rejects commands). The gate's own malformed-config handling
    /// fails the allowlist closed (denied) — a security config stays deny-by-default.</summary>
    public static RepoCommandConfig Parse(Repository repository)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<RepoCommandConfigDto>(
                string.IsNullOrWhiteSpace(repository.Configuration) ? "{}" : repository.Configuration!, ConfigJsonOptions);
            if (dto is null)
                return RepoCommandConfig.Defaults();
            return new RepoCommandConfig(
                dto.PermittedCommands ?? AllCommands(),
                dto.PermittedWorkItemTypes ?? new List<string> { WorkItemTypes.Issue, WorkItemTypes.PullRequest },
                dto.Authorization?.MinimumPermission);
        }
        catch (JsonException)
        {
            return RepoCommandConfig.Defaults();
        }
    }

    internal static List<string> AllCommands() =>
    [
        AgentCommandNames.Plan, AgentCommandNames.Code, AgentCommandNames.Review, AgentCommandNames.TestCases,
        AgentCommandNames.Status, AgentCommandNames.Stop, AgentCommandNames.Help, AgentCommandNames.Retry,
    ];

    private sealed class RepoCommandConfigDto
    {
        [JsonPropertyName("permitted_commands")] public List<string>? PermittedCommands { get; set; }
        [JsonPropertyName("permitted_work_item_types")] public List<string>? PermittedWorkItemTypes { get; set; }
        [JsonPropertyName("authorization")] public AuthorizationConfigDto? Authorization { get; set; }

        internal sealed class AuthorizationConfigDto
        {
            [JsonPropertyName("minimum_permission")] public string? MinimumPermission { get; set; }
        }
    }
}
