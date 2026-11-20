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
    public DbSet<SyncCursor> SyncCursors => Set<SyncCursor>();
    public DbSet<AgentProfile> AgentProfiles => Set<AgentProfile>();
    public DbSet<AgentProfileVersion> AgentProfileVersions => Set<AgentProfileVersion>();
    public DbSet<AgentProfileRoutingRule> AgentProfileRoutingRules => Set<AgentProfileRoutingRule>();

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
    }
}
