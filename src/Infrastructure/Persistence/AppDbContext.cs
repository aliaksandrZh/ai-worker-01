using Microsoft.EntityFrameworkCore;

namespace PiAgent.Infrastructure.Persistence;

/// <summary>
/// Control-plane orchestration state (spec decision 19). Task 01 shipped the initial (near-empty)
/// migration; task 02 adds the configuration-plane entities; task 07 adds the orchestration core
/// (Workflow, Task, Run, AgentSession, OrchestrationJob, ContainerInstance, TaskArtifactInput, Artifact).
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Repository> Repositories => Set<Repository>();
    public DbSet<OutboxAction> OutboxActions => Set<OutboxAction>();
    public DbSet<SyncCursor> SyncCursors => Set<SyncCursor>();
    public DbSet<AgentProfile> AgentProfiles => Set<AgentProfile>();
    public DbSet<AgentProfileVersion> AgentProfileVersions => Set<AgentProfileVersion>();
    public DbSet<AgentProfileRoutingRule> AgentProfileRoutingRules => Set<AgentProfileRoutingRule>();
    public DbSet<BuildJob> BuildJobs => Set<BuildJob>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<WorkItemLink> WorkItemLinks => Set<WorkItemLink>();
    public DbSet<GitHubComment> GitHubComments => Set<GitHubComment>();
    public DbSet<GitHubReview> GitHubReviews => Set<GitHubReview>();
    public DbSet<GitHubEvent> GitHubEvents => Set<GitHubEvent>();
    public DbSet<AgentCommand> AgentCommands => Set<AgentCommand>();
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<OrchestrationTask> OrchestrationTasks => Set<OrchestrationTask>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<AgentSession> AgentSessions => Set<AgentSession>();
    public DbSet<OrchestrationJob> OrchestrationJobs => Set<OrchestrationJob>();
    public DbSet<ContainerInstance> ContainerInstances => Set<ContainerInstance>();
    public DbSet<TaskArtifactInput> TaskArtifactInputs => Set<TaskArtifactInput>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<RunEvent> RunEvents => Set<RunEvent>();
    public DbSet<RunCredential> RunCredentials => Set<RunCredential>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<RunContextSnapshot> RunContextSnapshots => Set<RunContextSnapshot>();
    public DbSet<MonitorObservation> MonitorObservations => Set<MonitorObservation>();
    public DbSet<OrphanCandidate> OrphanCandidates => Set<OrphanCandidate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Repository>(entity =>
        {
            entity.ToTable("repository");
            entity.HasIndex(r => r.GithubRepositoryId).IsUnique();
            entity.HasIndex(r => r.FullName).IsUnique();
            entity.Property(r => r.Configuration).HasColumnType("jsonb");
        });

        modelBuilder.Entity<SyncCursor>(entity =>
        {
            entity.ToTable("sync_cursor");
            entity.HasIndex(c => new { c.RepositoryId, c.Stream }).IsUnique();
            entity.HasOne<Repository>()
                .WithMany()
                .HasForeignKey(c => c.RepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentProfile>(entity =>
        {
            entity.ToTable("agent_profile");
            entity.HasIndex(p => p.Name).IsUnique();
        });

        modelBuilder.Entity<AgentProfileVersion>(entity =>
        {
            entity.ToTable("agent_profile_version");
            entity.HasIndex(v => new { v.AgentProfileId, v.Version }).IsUnique();
            entity.Property(v => v.ConfigSnapshot).HasColumnType("jsonb");
            entity.Property(v => v.SkillsConfiguration).HasColumnType("jsonb");
            entity.Property(v => v.McpConfiguration).HasColumnType("jsonb");
            entity.Property(v => v.ToolsConfiguration).HasColumnType("jsonb");
            entity.Property(v => v.EnvironmentConfiguration).HasColumnType("jsonb");
            entity.Property(v => v.ResourceConfiguration).HasColumnType("jsonb");
            entity.Property(v => v.Capabilities).HasColumnType("jsonb");
            entity.Property(v => v.RetryPolicy).HasColumnType("jsonb");
            entity.Property(v => v.PermissionPolicy).HasColumnType("jsonb");
            entity.Property(v => v.BridgeConfiguration).HasColumnType("jsonb");
            entity.HasOne<AgentProfile>()
                .WithMany()
                .HasForeignKey(v => v.AgentProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ---- build pipeline (task 10, 02 §12b/decision 81) ----

        // (comment markers preserved in the migration below)
        modelBuilder.Entity<BuildJob>(entity =>
        {
            entity.ToTable("build_job");
            // One BuildJob per AgentProfileVersion (02 §12b); retries reuse the same buildId
            entity.HasIndex(j => j.AgentProfileVersionId).IsUnique();
            entity.HasIndex(j => new { j.State, j.NextRetryAt });
            entity.Property(j => j.DispatchPayload).HasColumnType("jsonb");
            entity.HasOne<AgentProfileVersion>()
                .WithMany()
                .HasForeignKey(j => j.AgentProfileVersionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AgentProfileRoutingRule>(entity =>
        {
            entity.ToTable("agent_profile_routing_rule");
            entity.HasIndex(r => new { r.RepositoryId, r.TaskType, r.Priority }).IsUnique();
            entity.HasOne<AgentProfileVersion>()
                .WithMany()
                .HasForeignKey(r => r.AgentProfileVersionId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<OutboxAction>(entity =>
        {
            entity.ToTable("outbox_action");
            entity.Property(a => a.Payload).HasColumnType("jsonb");
            entity.Property(a => a.ActionType).HasDefaultValue(OutboxActionTypes.Comment);
            entity.Property(a => a.Status).HasDefaultValue(OutboxStatuses.Pending);
            entity.HasIndex(a => a.IdempotencyKey).IsUnique();
            entity.HasIndex(a => new { a.Status, a.AvailableAt });
            entity.HasOne<Repository>()
                .WithMany()
                .HasForeignKey(a => a.RepositoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<WorkItem>(entity =>
        {
            entity.ToTable("work_item");
            entity.HasIndex(w => new { w.RepositoryId, w.Number }).IsUnique();
            entity.HasIndex(w => w.GithubNodeId).IsUnique();
            entity.Property(w => w.Assignees).HasColumnType("jsonb");
            entity.Property(w => w.Labels).HasColumnType("jsonb");
            entity.Property(w => w.RequestedReviewers).HasColumnType("jsonb");
            entity.HasOne<Repository>()
                .WithMany()
                .HasForeignKey(w => w.RepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkItemLink>(entity =>
        {
            entity.ToTable("work_item_link");
            // (issue, pr, relation) unique — 02 §4/§23 source/target uniqueness in typed-role shape
            entity.HasIndex(l => new { l.IssueWorkItemId, l.PrWorkItemId, l.RelationType }).IsUnique();
            entity.HasOne<WorkItem>()
                .WithMany()
                .HasForeignKey(l => l.IssueWorkItemId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<WorkItem>()
                .WithMany()
                .HasForeignKey(l => l.PrWorkItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GitHubComment>(entity =>
        {
            entity.ToTable("github_comment");
            entity.HasIndex(c => new { c.Kind, c.GithubCommentId }).IsUnique();
            entity.HasIndex(c => new { c.WorkItemId, c.CreatedAtGithub });
            // github_review_id is an external ID only — no DB FK (02 §5)
            entity.HasOne<WorkItem>()
                .WithMany()
                .HasForeignKey(c => c.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GitHubReview>(entity =>
        {
            entity.ToTable("github_review");
            entity.HasIndex(r => r.GithubReviewId).IsUnique();
            entity.HasOne<WorkItem>()
                .WithMany()
                .HasForeignKey(r => r.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GitHubEvent>(entity =>
        {
            entity.ToTable("github_event");
            // (source, dedupe_key) unique, 02 §23 — PostgreSQL NULLs are distinct, so unkeyed events
            // need no partial filter
            entity.HasIndex(e => new { e.Source, e.DedupeKey }).IsUnique();
            entity.HasIndex(e => new { e.RepositoryId, e.ProcessingStatus, e.ReceivedAt });
            entity.Property(e => e.Payload).HasColumnType("jsonb");
            entity.Property(e => e.ProcessingStatus).HasDefaultValue(GitHubEventStatuses.Pending);
            entity.HasOne<Repository>()
                .WithMany()
                .HasForeignKey(e => e.RepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<WorkItem>()
                .WithMany()
                .HasForeignKey(e => e.WorkItemId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AgentCommand>(entity =>
        {
            entity.ToTable("agent_command");
            // The command idempotency key (02 §11/§23): one comment ever produces one AgentCommand
            entity.HasIndex(c => c.GithubCommentId).IsUnique();
            entity.HasIndex(c => new { c.Status, c.CreatedAt });
            entity.Property(c => c.Arguments).HasColumnType("jsonb");
            entity.HasOne<WorkItem>()
                .WithMany()
                .HasForeignKey(c => c.WorkItemId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ---- orchestration core (task 07, 02 §9/§10/§10a/§13/§14/§15/§19) ----

        modelBuilder.Entity<Workflow>(entity =>
        {
            entity.ToTable("workflow");
            // One long-lived Workflow per root WorkItem in V1; reopen never resurrects a closed one (decisions 9/73)
            entity.HasIndex(w => w.WorkItemId).IsUnique();
            entity.HasIndex(w => new { w.Status, w.UpdatedAt });
            entity.HasOne<WorkItem>()
                .WithMany()
                .HasForeignKey(w => w.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrchestrationTask>(entity =>
        {
            entity.ToTable("task");
            entity.HasIndex(t => new { t.WorkflowId, t.Sequence }).IsUnique();
            entity.HasIndex(t => new { t.Status, t.CreatedAt });
            // One command creates at most one Task (02 §23)
            entity.HasIndex(t => t.TriggerCommandId)
                .IsUnique()
                .HasFilter("trigger_command_id IS NOT NULL");
            // The one-unfinished-Task-per-Workflow invariant (02 §23, decision 79) — admission applies
            // the lock + recheck; this index is the backstop. Snake_case names match the shipped schema.
            entity.HasIndex(t => t.WorkflowId)
                .IsUnique()
                .HasFilter("status IN ('queued', 'claimed', 'starting', 'running')")
                .HasDatabaseName("ux_task_one_unfinished_per_workflow");
            entity.Property(t => t.RetryPolicy).HasColumnType("jsonb");
            entity.HasOne<Workflow>()
                .WithMany()
                .HasForeignKey(t => t.WorkflowId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AgentProfileVersion>()
                .WithMany()
                .HasForeignKey(t => t.AgentProfileVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentCommand>()
                .WithMany()
                .HasForeignKey(t => t.TriggerCommandId)
                .OnDelete(DeleteBehavior.Restrict);
            // Ticket 19: one system trigger event creates at most one automatic Task
            entity.HasIndex(t => t.TriggerGithubEventId)
                .IsUnique()
                .HasFilter("trigger_github_event_id IS NOT NULL");
            entity.HasOne<GitHubEvent>()
                .WithMany()
                .HasForeignKey(t => t.TriggerGithubEventId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Run>(entity =>
        {
            entity.ToTable("run");
            entity.HasIndex(r => new { r.TaskId, r.AttemptNumber }).IsUnique();
            entity.HasOne<OrchestrationTask>()
                .WithMany()
                .HasForeignKey(r => r.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentSession>(entity =>
        {
            entity.ToTable("agent_session");
            // One-shot 1:1 with Run (decision 57)
            entity.HasIndex(s => s.RunId).IsUnique();
            entity.Property(s => s.Metadata).HasColumnType("jsonb");
            entity.Property(s => s.StorageStatus).HasDefaultValue(AgentSessionStorageStatuses.Available);
            entity.HasOne<Run>()
                .WithMany()
                .HasForeignKey(s => s.RunId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<OrchestrationTask>()
                .WithMany()
                .HasForeignKey(s => s.TaskId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentProfileVersion>()
                .WithMany()
                .HasForeignKey(s => s.AgentProfileVersionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OrchestrationJob>(entity =>
        {
            entity.ToTable("orchestration_job");
            entity.HasIndex(j => new { j.Status, j.AvailableAt, j.Priority });
            entity.HasOne<OrchestrationTask>()
                .WithMany()
                .HasForeignKey(j => j.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ContainerInstance>(entity =>
        {
            entity.ToTable("container_instance");
            entity.HasOne<AgentSession>()
                .WithMany()
                .HasForeignKey(c => c.AgentSessionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Run>()
                .WithMany()
                .HasForeignKey(c => c.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskArtifactInput>(entity =>
        {
            entity.ToTable("task_artifact_input");
            entity.HasIndex(i => new { i.TaskId, i.ArtifactId }).IsUnique();
            entity.HasOne<OrchestrationTask>()
                .WithMany()
                .HasForeignKey(i => i.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Artifact>()
                .WithMany()
                .HasForeignKey(i => i.ArtifactId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Artifact>(entity =>
        {
            entity.ToTable("artifact");
            entity.Property(a => a.Metadata).HasColumnType("jsonb");
            entity.HasOne<OrchestrationTask>()
                .WithMany()
                .HasForeignKey(a => a.TaskId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Workflow>()
                .WithMany()
                .HasForeignKey(a => a.WorkflowId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ---- run pipeline (task 09, 02 §14a/§16/§18) ----

        modelBuilder.Entity<RunEvent>(entity =>
        {
            entity.ToTable("run_event");
            // The callback idempotency key (02 §18, 04 §8)
            entity.HasIndex(e => new { e.RunId, e.EventId }).IsUnique();
            entity.HasIndex(e => new { e.RunId, e.ReceivedAt });
            entity.Property(e => e.Payload).HasColumnType("jsonb");
            entity.HasOne<Run>()
                .WithMany()
                .HasForeignKey(e => e.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RunCredential>(entity =>
        {
            entity.ToTable("run_credential");
            // One credential per Run, scoped to it (decision 60)
            entity.HasIndex(c => c.RunId).IsUnique();
            entity.Property(c => c.TokenHash).HasColumnType("varchar(64)");
            entity.HasOne<Run>()
                .WithMany()
                .HasForeignKey(c => c.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Workspace>(entity =>
        {
            entity.ToTable("workspace");
            // One Workspace per Task (02 §16/§23)
            entity.HasIndex(w => w.TaskId).IsUnique();
            entity.HasOne<OrchestrationTask>()
                .WithMany()
                .HasForeignKey(w => w.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RunContextSnapshot>(entity =>
        {
            entity.ToTable("run_context_snapshot");
            // Immutable, one per Run (02 §14a/§23)
            entity.HasIndex(s => s.RunId).IsUnique();
            entity.Property(s => s.IncludedSources).HasColumnType("jsonb");
            entity.Property(s => s.ExcludedSources).HasColumnType("jsonb");
            // ticket 11: content is a `text` blob — jsonb would reorder keys and re-space the JSON on
            // storage, breaking the frozen-bytes identity (the content_hash and budget both apply to the
            // EXACT serialized input the Bridge gets via /input, 02 §14a + decision 63).
            entity.Property(s => s.Content).HasColumnType("text");
            entity.HasOne<Run>()
                .WithMany()
                .HasForeignKey(s => s.RunId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<OrchestrationTask>()
                .WithMany()
                .HasForeignKey(s => s.TaskId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ---- host monitor + reconciliation (task 13, decisions 62/68/77) ----

        modelBuilder.Entity<MonitorObservation>(entity =>
        {
            entity.ToTable("monitor_observation");
            // Latest-facts store (single row): reconciliation reads only the newest observation.
            entity.Property(o => o.Containers).HasColumnType("jsonb");
            entity.Property(o => o.Storage).HasColumnType("jsonb");
        });

        modelBuilder.Entity<OrphanCandidate>(entity =>
        {
            entity.ToTable("orphan_candidate");
            entity.HasIndex(c => c.DirectoryName).IsUnique();
            entity.HasIndex(c => new { c.Status, c.UpdatedAt });
        });
    }
}
