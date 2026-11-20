using Microsoft.EntityFrameworkCore;

namespace PiAgent.Infrastructure.Persistence;

/// <summary>
/// Control-plane orchestration state (spec decision 19). Starts near-empty (task 01 — initial
/// migration holds no application tables); entities are added by later tasks.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
    }
}
