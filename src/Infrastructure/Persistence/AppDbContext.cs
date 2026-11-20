using Microsoft.EntityFrameworkCore;

namespace PiAgent.Infrastructure.Persistence;

/// <summary>
/// Control-plane orchestration state (spec decision 19). Task 01 shipped the initial (near-empty)
/// migration; task 02 adds the configuration-plane entities: Repository, SyncCursor, AgentProfile,
/// AgentProfileVersion, AgentProfileRoutingRule (02 §2/§8/§12/§12a).
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Repository> Repositories => Set<Repository>();
    public DbSet<OutboxAction> OutboxActions => Set<OutboxAction>();
    public DbSet<SyncCursor> SyncCursors => Set<SyncCursor>();
    public DbSet<AgentProfile> AgentProfiles => Set<AgentProfile>();
    public DbSet<AgentProfileVersion> AgentProfileVersions => Set<AgentProfileVersion>();
    public DbSet<AgentProfileRoutingRule> AgentProfileRoutingRules => Set<AgentProfileRoutingRule>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<GitHubComment> GitHubComments => Set<GitHubComment>();
    public DbSet<GitHubReview> GitHubReviews => Set<GitHubReview>();
    public DbSet<GitHubEvent> GitHubEvents => Set<GitHubEvent>();

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
    }
}
