using Intune.WipeMonitor.Models;
using Intune.WipeMonitor.Shared;
using Microsoft.EntityFrameworkCore;

namespace Intune.WipeMonitor.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<DeviceCleanupRecord> DeviceCleanupRecords => Set<DeviceCleanupRecord>();
    public DbSet<CleanupStepLog> CleanupStepLogs => Set<CleanupStepLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceCleanupRecord>(entity =>
        {
            entity.HasKey(e => e.WipeActionId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.DeviceDisplayName);
            entity.HasIndex(e => e.ManagedDeviceId);

            entity.HasMany(e => e.CleanupSteps)
                  .WithOne(s => s.DeviceCleanupRecord)
                  .HasForeignKey(s => s.WipeActionId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CleanupStepLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.WipeActionId, e.Target });
        });
    }
}
