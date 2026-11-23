using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Commands;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>
/// Routing-rule/profile resolution for one task type: the repository-specific
/// enabled rule wins over the global one, higher priority first; the profile must be enabled and the
/// pinned version `ready`. Shared by command admission and the /help availability filter
/// so both apply the identical gate: missing/disabled → `agent_profile_not_configured`,
/// known but not `ready` → `agent_image_not_ready`.
/// </summary>
public static class ProfileGate
{
    public sealed record Resolution(AgentProfileVersion Version, AgentProfile Profile);

    /// <summary>Resolved (Version, Profile) or the admission-style rejection with reason + short detail.</summary>
    public static async Task<(Resolution? Resolved, string? Reason, string? Detail)> ResolveAsync(
        AppDbContext db, Repository repository, string taskType, CancellationToken cancellationToken)
    {
        var rule = await db.AgentProfileRoutingRules
            .Where(r => r.TaskType == taskType && r.IsEnabled
                && (r.RepositoryId == repository.Id || r.RepositoryId == null))
            .OrderBy(r => r.RepositoryId == repository.Id ? 0 : 1)
            .ThenByDescending(r => r.Priority)
            .FirstOrDefaultAsync(cancellationToken);
        if (rule is null)
            return (null, AgentCommandRejectionReasons.AgentProfileNotConfigured,
                $"no routing rule maps task type '{taskType}'");

        var version = await db.AgentProfileVersions.SingleAsync(
            v => v.Id == rule.AgentProfileVersionId, cancellationToken);
        var profile = await db.AgentProfiles.SingleAsync(p => p.Id == version.AgentProfileId, cancellationToken);
        if (!profile.IsEnabled || version.State == ProfileVersionStates.Disabled)
            return (null, AgentCommandRejectionReasons.AgentProfileNotConfigured,
                $"profile '{profile.Name}' is disabled");
        if (version.State != ProfileVersionStates.Ready)
            return (null, AgentCommandRejectionReasons.AgentImageNotReady,
                $"profile version state is '{version.State}'");

        return (new Resolution(version, profile), null, null);
    }
}
