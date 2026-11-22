using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PiAgent.Infrastructure.Persistence;
using Xunit;

namespace PiAgent.Infrastructure.Tests.Persistence;

/// <summary>
/// Orchestration-core metadata per 02 §9/§10/§10a/§13/§14/§15/§19/§23 (task 07): one long-lived
/// Workflow per WorkItem, the one-unfinished-Task-per-Workflow partial unique index, 1:1 Run↔AgentSession
/// (run_id unique), attempt uniqueness, Queue FIFO index, ContainerInstance desired/observed state
/// columns, and the TaskArtifactInput join (unique task/artifact pair).
/// </summary>
public class OrchestratorCoreModelTests
{
    private static AppDbContext NewDbContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=test;Username=test")
            .UseSnakeCaseNamingConvention()
            .Options);

    private static IEntityType Entity(AppDbContext dbContext, Type type) =>
        dbContext.Model.FindEntityType(type)
        ?? throw new InvalidOperationException($"{type.Name} is not in the model");

    private static IProperty Property(IEntityType entity, string name) =>
        entity.FindProperty(name) ?? throw new InvalidOperationException($"{name} missing on {entity.DisplayName()}");

    private static IIndex Index(IEntityType entity, params string[] properties) =>
        entity.GetIndexes().Single(i => i.Properties.Select(p => p.Name).SequenceEqual(properties));

    private static IForeignKey Fk(IEntityType entity, Type principal) =>
        entity.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == principal);

    // ---- Workflow (02 §9) ----

    [Fact]
    public void Workflow_TableName_AndRequiredColumns()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(Workflow));
        Assert.Equal("workflow", entity.GetTableName());

        var properties = entity.GetProperties().ToDictionary(p => p.Name);
        Assert.Equal(10, properties.Count);
        foreach (var required in new[] { nameof(Workflow.Id), nameof(Workflow.WorkItemId),
                     nameof(Workflow.Status), nameof(Workflow.CreatedAt), nameof(Workflow.UpdatedAt) })
            Assert.False(properties[required].IsNullable, $"{required} must be required");
        foreach (var nullable in new[] { nameof(Workflow.CurrentTaskId), nameof(Workflow.StartedAt),
                     nameof(Workflow.ClosedAt), nameof(Workflow.FailureReason), nameof(Workflow.InitialReviewCreatedAt) })
            Assert.True(properties[nullable].IsNullable, $"{nullable} must be nullable");
    }

    [Fact]
    public void Workflow_Indexes()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(Workflow));
        Assert.True(Index(entity, nameof(Workflow.WorkItemId)).IsUnique); // one long-lived workflow per WorkItem (02 §9)
        Assert.False(Index(entity, nameof(Workflow.Status), nameof(Workflow.UpdatedAt)).IsUnique);
    }

    // ---- OrchestrationTask (02 §10) ----

    [Fact]
    public void OrchestrationTask_TableName_AndColumns()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(OrchestrationTask));
        Assert.Equal("task", entity.GetTableName());

        var properties = entity.GetProperties().ToDictionary(p => p.Name);
        Assert.Equal(16, properties.Count);
        foreach (var required in new[] { nameof(OrchestrationTask.Id), nameof(OrchestrationTask.WorkflowId),
                     nameof(OrchestrationTask.Sequence), nameof(OrchestrationTask.Type),
                     nameof(OrchestrationTask.AgentProfileVersionId), nameof(OrchestrationTask.Status),
                     nameof(OrchestrationTask.CreatedAt), nameof(OrchestrationTask.RetryPolicy) })
            Assert.False(properties[required].IsNullable, $"{required} must be required");
        foreach (var nullable in new[] { nameof(OrchestrationTask.TriggerCommandId),
                     nameof(OrchestrationTask.TargetHeadSha), nameof(OrchestrationTask.TargetBaseSha),
                     nameof(OrchestrationTask.ClaimedAt), nameof(OrchestrationTask.StartedAt),
                     nameof(OrchestrationTask.CompletedAt), nameof(OrchestrationTask.CancelledAt),
                     nameof(OrchestrationTask.FailureReason) })
            Assert.True(properties[nullable].IsNullable, $"{nullable} must be nullable");
        Assert.Equal("jsonb", properties[nameof(OrchestrationTask.RetryPolicy)].GetColumnType());
    }

    [Fact]
    public void OrchestrationTask_UniqueWorkflowSequence()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(OrchestrationTask));
        Assert.True(Index(entity, nameof(OrchestrationTask.WorkflowId), nameof(OrchestrationTask.Sequence)).IsUnique);
    }

    [Fact]
    public void OrchestrationTask_PartialIndexes()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(OrchestrationTask));
        Assert.False(entity.GetIndexes().Single(i => i.Properties.Select(p => p.Name)
            .SequenceEqual([nameof(OrchestrationTask.Status), nameof(OrchestrationTask.CreatedAt)])).IsUnique);

        var unfinished = entity.GetIndexes()
            .Single(i => i.GetFilter() is { } filter && filter.Contains("queued")
                && filter.Contains("claimed") && filter.Contains("starting") && filter.Contains("running"));
        Assert.True(unfinished.IsUnique);
        Assert.Equal("ux_task_one_unfinished_per_workflow", unfinished.GetDatabaseName());

        var triggerCommand = entity.GetIndexes()
            .Single(i => i.GetFilter() is { } filter
                && filter.Contains("IS NOT NULL")
                && i.Properties.Select(p => p.Name).SequenceEqual([nameof(OrchestrationTask.TriggerCommandId)]));
        Assert.True(triggerCommand.IsUnique);
    }

    [Fact]
    public void OrchestrationTask_FkDeleteBehaviors()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(OrchestrationTask));
        Assert.Equal(DeleteBehavior.Cascade, Fk(entity, typeof(Workflow)).DeleteBehavior);
        Assert.Equal(DeleteBehavior.Restrict, Fk(entity, typeof(AgentProfileVersion)).DeleteBehavior);
        Assert.Equal(DeleteBehavior.Restrict, Fk(entity, typeof(AgentCommand)).DeleteBehavior);
    }

    // ---- Run (02 §14) ----

    [Fact]
    public void Run_TableName_AndUniqueTaskAttempt()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(Run));
        Assert.Equal("run", entity.GetTableName());

        var properties = entity.GetProperties().ToDictionary(p => p.Name);
        Assert.Equal(18, properties.Count); // ticket 14: +stop_requested_at, +stop_reason
        foreach (var required in new[] { nameof(Run.Id), nameof(Run.TaskId),
                     nameof(Run.AttemptNumber), nameof(Run.Status) })
            Assert.False(properties[required].IsNullable, $"{required} must be required");
        foreach (var nullable in new[] { nameof(Run.StartedAt), nameof(Run.CompletedAt),
                     nameof(Run.LastHeartbeatAt), nameof(Run.LastProgressAt), nameof(Run.ExitCode),
                     nameof(Run.ErrorCode), nameof(Run.ErrorMessage), nameof(Run.ResultSummary),
                     nameof(Run.LogStorageReference), nameof(Run.LogSizeBytes),
                     nameof(Run.LogsCompletedAt), nameof(Run.LogsDeletedAt) })
            Assert.True(properties[nullable].IsNullable, $"{nullable} must be nullable");

        Assert.True(Index(entity, nameof(Run.TaskId), nameof(Run.AttemptNumber)).IsUnique);
        Assert.Equal(DeleteBehavior.Cascade, Fk(entity, typeof(OrchestrationTask)).DeleteBehavior);
    }

    [Fact]
    public void RunStatuses_IncludeStoppingAndTimedOut()
    {
        Assert.Equal("stopping", RunStatuses.Stopping); // decision 75 cap / awaiting confirmed stop (ticket 14)
        Assert.Equal("timed_out", RunStatuses.TimedOut);
    }

    // ---- AgentSession (02 §13, decision 57) ----

    [Fact]
    public void AgentSession_RunIdUnique_AndFks()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(AgentSession));
        Assert.Equal("agent_session", entity.GetTableName());

        var runIdIndex = entity.GetIndexes().Single(i => i.Properties.Select(p => p.Name)
            .SequenceEqual([nameof(AgentSession.RunId)]));
        Assert.True(runIdIndex.IsUnique);
        Assert.Equal(DeleteBehavior.Cascade, Fk(entity, typeof(Run)).DeleteBehavior);
        Assert.Equal(DeleteBehavior.Restrict, Fk(entity, typeof(OrchestrationTask)).DeleteBehavior);
        Assert.Equal(DeleteBehavior.Restrict, Fk(entity, typeof(AgentProfileVersion)).DeleteBehavior);
        Assert.Equal(AgentSessionStorageStatuses.Available,
            Property(entity, nameof(AgentSession.StorageStatus)).GetDefaultValue());
    }

    // ---- OrchestrationJob (02 §19) ----

    [Fact]
    public void OrchestrationJob_QueueIndex()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(OrchestrationJob));
        Assert.Equal("orchestration_job", entity.GetTableName());

        Assert.False(Index(entity, nameof(OrchestrationJob.Status), nameof(OrchestrationJob.AvailableAt),
            nameof(OrchestrationJob.Priority)).IsUnique);
        Assert.Equal(DeleteBehavior.Cascade, Fk(entity, typeof(OrchestrationTask)).DeleteBehavior);
    }

    // ---- ContainerInstance (02 §15) ----

    [Fact]
    public void ContainerInstance_StatesAndFks()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(ContainerInstance));
        Assert.Equal("container_instance", entity.GetTableName());

        var properties = entity.GetProperties().ToDictionary(p => p.Name);
        Assert.Equal(13, properties.Count);
        foreach (var required in new[] { nameof(ContainerInstance.Id), nameof(ContainerInstance.AgentSessionId),
                     nameof(ContainerInstance.DesiredState), nameof(ContainerInstance.ObservedState),
                     nameof(ContainerInstance.CreatedAt) })
            Assert.False(properties[required].IsNullable, $"{required} must be required");
        foreach (var nullable in new[] { nameof(ContainerInstance.RunId), nameof(ContainerInstance.KomodoDeploymentId),
                     nameof(ContainerInstance.DeploymentName), nameof(ContainerInstance.HostId),
                     nameof(ContainerInstance.ContainerImage), nameof(ContainerInstance.LastStartedAt),
                     nameof(ContainerInstance.LastStoppedAt), nameof(ContainerInstance.RemovedAt) })
            Assert.True(properties[nullable].IsNullable, $"{nullable} must be nullable");

        Assert.Equal(DeleteBehavior.Cascade, Fk(entity, typeof(AgentSession)).DeleteBehavior);
        Assert.Equal(DeleteBehavior.Cascade, Fk(entity, typeof(Run)).DeleteBehavior);
    }

    // ---- TaskArtifactInput (02 §10a) ----

    [Fact]
    public void TaskArtifactInput_UniqueTaskArtifact()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(TaskArtifactInput));
        Assert.Equal("task_artifact_input", entity.GetTableName());

        Assert.True(Index(entity, nameof(TaskArtifactInput.TaskId), nameof(TaskArtifactInput.ArtifactId)).IsUnique);
        Assert.Equal(DeleteBehavior.Cascade, Fk(entity, typeof(OrchestrationTask)).DeleteBehavior);
        Assert.Equal(DeleteBehavior.Restrict, Fk(entity, typeof(Artifact)).DeleteBehavior);
    }

    // ---- Artifact (02 §17) ----

    [Fact]
    public void Artifact_TableName_AndFks()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(Artifact));
        Assert.Equal("artifact", entity.GetTableName());

        var properties = entity.GetProperties().ToDictionary(p => p.Name);
        Assert.Equal(8, properties.Count);
        foreach (var required in new[] { nameof(Artifact.Id), nameof(Artifact.TaskId), nameof(Artifact.WorkflowId),
                     nameof(Artifact.Type), nameof(Artifact.Format), nameof(Artifact.CreatedAt) })
            Assert.False(properties[required].IsNullable, $"{required} must be required");
        foreach (var nullable in new[] { nameof(Artifact.Content), nameof(Artifact.Metadata) })
            Assert.True(properties[nullable].IsNullable, $"{nullable} must be nullable");

        Assert.Equal(DeleteBehavior.Restrict, Fk(entity, typeof(OrchestrationTask)).DeleteBehavior);
        Assert.Equal(DeleteBehavior.Restrict, Fk(entity, typeof(Workflow)).DeleteBehavior);
    }

    [Fact]
    public void ArtifactFormats_AreTheThreeSupportedV1Formats()
    {
        Assert.Equal("markdown", ArtifactFormats.Markdown);
        Assert.Equal("json", ArtifactFormats.Json);
        Assert.Equal("text", ArtifactFormats.Text);
    }
}
