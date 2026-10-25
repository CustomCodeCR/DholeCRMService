using Dhole.Crm.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Infrastructure.Persistence;

public sealed partial class CrmDbContext
{
    public DbSet<CrmExternalMapping> ExternalMappings => Set<CrmExternalMapping>();
    public DbSet<CrmSyncJob> SyncJobs => Set<CrmSyncJob>();
    public DbSet<CrmSyncConflict> SyncConflicts => Set<CrmSyncConflict>();

    private static void ConfigureIntegrations(ModelBuilder b)
    {
        ConfigureRecord<CrmExternalMapping>(b, "crm_external_mappings");
        ConfigureRecord<CrmSyncJob>(b, "crm_sync_jobs");
        ConfigureRecord<CrmSyncConflict>(b, "crm_sync_conflicts");
        b.Entity<CrmExternalMapping>(e =>
        {
            e.Property(x => x.ExternalSystem).HasMaxLength(32);
            e.Property(x => x.EntityType).HasMaxLength(32);
            e.Property(x => x.ExternalId).HasMaxLength(128);
            e.Property(x => x.ExternalVersion).HasMaxLength(160);
            e.Property(x => x.LastPayloadHash).HasMaxLength(64);
            e.Property(x => x.SyncState).HasMaxLength(32);
            e.HasIndex(x => new { x.ExternalSystem, x.EntityType, x.ExternalId }).IsUnique();
            e.HasIndex(x => new { x.ExternalSystem, x.EntityType, x.EntityId }).IsUnique();
        });
        b.Entity<CrmSyncJob>(e =>
        {
            e.Property(x => x.SourceSystem).HasMaxLength(32);
            e.Property(x => x.Direction).HasMaxLength(16);
            e.Property(x => x.EntityType).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(24);
            e.Property(x => x.LastErrorCode).HasMaxLength(80);
            e.Property(x => x.ConfigurationHash).HasMaxLength(64);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
        });
        b.Entity<CrmSyncConflict>(e =>
        {
            e.Property(x => x.EntityType).HasMaxLength(32);
            e.Property(x => x.ExternalId).HasMaxLength(128);
            e.Property(x => x.ReasonCode).HasMaxLength(64);
            e.Property(x => x.Resolution).HasMaxLength(24);
            e.HasOne<CrmSyncJob>().WithMany().HasForeignKey(x => x.SyncJobId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.SyncJobId, x.EntityType, x.ExternalId, x.ReasonCode }).IsUnique();
        });
    }
}
