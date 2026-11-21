using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PiAgent.Infrastructure.Persistence;
using Xunit;

namespace PiAgent.Infrastructure.Tests.Persistence;

/// <summary>
/// Run-pipeline metadata per 02 §14a/§16/§18/§23 (task 09): the RunEvent table (unique
/// (run_id, event_id) idempotency pair, the 04 §4 type vocabulary incl. the progress split),
/// the per-Run credential (one row per run), the Task-owned Workspace (unique task_id), and the
/// immutable RunContextSnapshot (unique run_id, the frozen content).
/// </summary>
public class RunPipelineModelTests
{
    private static AppDbContext NewDbContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=test;Username=test")
            .Options);

    private static IEntityType Entity(AppDbContext dbContext, Type type) =>
        dbContext.Model.FindEntityType(type)
        ?? throw new InvalidOperationException($"{type.Name} is not in the model");

    private static IProperty Property(IEntityType entity, string name) =>
        entity.FindProperty(name) ?? throw new InvalidOperationException($"{name} missing on {entity.DisplayName()}");

    [Fact]
    public void RunEvent_UniquePairAndVocabulary()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(RunEvent));
        Assert.Equal("run_event", entity.GetTableName());

        var index = entity.GetIndexes().Single(i => i.Properties.Select(p => p.Name).SequenceEqual(
            [nameof(RunEvent.RunId), nameof(RunEvent.EventId)]));
        Assert.True(index.IsUnique);

        Assert.Equal("jsonb", Property(entity, nameof(RunEvent.Payload)).GetColumnType());

        var expected = new[]
        {
            RunEventTypes.AgentStarted, RunEventTypes.TurnStarted, RunEventTypes.TurnCompleted,
            RunEventTypes.AssistantOutput, RunEventTypes.ToolStarted, RunEventTypes.ToolProgress,
            RunEventTypes.ToolCompleted, RunEventTypes.AgentSettled, RunEventTypes.Heartbeat,
            RunEventTypes.PermissionBlocked, RunEventTypes.ArtifactProduced, RunEventTypes.Completed,
            RunEventTypes.Failed, RunEventTypes.Cancelled,
        };
        Assert.Equal(expected.OrderBy(t => t), RunEventTypes.All.OrderBy(t => t));
        // progress never counts heartbeats or liveness (decision 58/68)
        Assert.DoesNotContain(RunEventTypes.Heartbeat, RunEventTypes.Progress);
        Assert.DoesNotContain(RunEventTypes.AgentStarted, RunEventTypes.Progress);
        Assert.Contains(RunEventTypes.ToolProgress, RunEventTypes.Progress);
        Assert.Contains(RunEventTypes.AssistantOutput, RunEventTypes.Progress);
    }

    [Fact]
    public void RunCredential_OneRowPerRun_HashOnly()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(RunCredential));
        Assert.Equal("run_credential", entity.GetTableName());
        Assert.True(entity.GetIndexes().Single(i => i.Properties.Single().Name == nameof(RunCredential.RunId)).IsUnique);
        Assert.True(Property(entity, nameof(RunCredential.RevokedAt)).IsNullable);
        Assert.False(Property(entity, nameof(RunCredential.TokenHash)).IsNullable);
    }

    [Fact]
    public void Workspace_TheTaskMachineColumns()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(Workspace));
        Assert.Equal("workspace", entity.GetTableName());
        var index = entity.GetIndexes().Single(i => i.Properties.Single().Name == nameof(Workspace.TaskId));
        Assert.True(index.IsUnique);

        foreach (var required in new[] { nameof(Workspace.WorkItemId), nameof(Workspace.RepositoryId),
                     nameof(Workspace.StorageKind), nameof(Workspace.AccessMode), nameof(Workspace.Status),
                     nameof(Workspace.StorageStatus), nameof(Workspace.CreatedAt) })
            Assert.False(Property(entity, required).IsNullable, $"{required} must be required");
        foreach (var nullable in new[] { nameof(Workspace.StorageReference), nameof(Workspace.BranchName),
                     nameof(Workspace.BaseSha), nameof(Workspace.CurrentSha), nameof(Workspace.LastUsedAt),
                     nameof(Workspace.DeletedAt) })
            Assert.True(Property(entity, nullable).IsNullable, $"{nullable} must be nullable");

        // 03 §6 status machine vocabulary (Provisioning → Ready → InUse → Ready; CleanupPending → Deleted)
        var statuses = new[] { WorkspaceStatuses.Provisioning, WorkspaceStatuses.Ready, WorkspaceStatuses.InUse,
            WorkspaceStatuses.Failed, WorkspaceStatuses.CleanupPending, WorkspaceStatuses.Deleted };
        Assert.Equal(6, statuses.Distinct().Count());
        Assert.Equal("host_path", WorkspaceStorageKinds.HostPath);
        Assert.Equal("read_write", WorkspaceAccessModes.ReadWrite);
        Assert.Equal("read_only", WorkspaceAccessModes.ReadOnly);
    }

    [Fact]
    public void RunContextSnapshot_ImmutableOnePerRun_WithFrozenContent()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(RunContextSnapshot));
        Assert.Equal("run_context_snapshot", entity.GetTableName());
        Assert.True(entity.GetIndexes().Single(i => i.Properties.Single().Name == nameof(RunContextSnapshot.RunId)).IsUnique);
        Assert.Equal("jsonb", Property(entity, nameof(RunContextSnapshot.Content)).GetColumnType());
        Assert.Equal("jsonb", Property(entity, nameof(RunContextSnapshot.IncludedSources)).GetColumnType());
        Assert.Equal("jsonb", Property(entity, nameof(RunContextSnapshot.ExcludedSources)).GetColumnType());
    }
}
