using Dhole.Crm.Domain;
using Microsoft.EntityFrameworkCore;
namespace Dhole.Crm.Infrastructure.Persistence;
public sealed partial class CrmDbContext
{
    private static void ConfigureFollowups(ModelBuilder b)
    {
        ConfigureRecord<AccountFollowupPolicy>(b,"crm_account_followup_policies");
        ConfigureRecord<Followup>(b,"crm_followups");
        ConfigureRecord<FollowupHistory>(b,"crm_followup_history");
        ConfigureRecord<CrmReminder>(b,"crm_reminders");
        b.Entity<AccountFollowupPolicy>(e => {
            e.HasOne<Account>().WithMany().HasForeignKey(x=>x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x=>x.ChannelStrategy).HasMaxLength(12);
            e.Property(x=>x.TimezoneId).HasMaxLength(128);
            e.HasIndex(x=>x.AccountId).IsUnique();
            e.HasIndex(x=>new{x.Enabled,x.NextDueAt});
        });
        b.Entity<Followup>(e => {
            e.HasOne<Account>().WithMany().HasForeignKey(x=>x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Lead>().WithMany().HasForeignKey(x=>x.LeadId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Opportunity>().WithMany().HasForeignKey(x=>x.OpportunityId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AccountFollowupPolicy>().WithMany().HasForeignKey(x=>x.PolicyId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x=>x.Channel).HasMaxLength(12);
            e.Property(x=>x.Status).HasMaxLength(22);
            e.HasIndex(x=>new{x.AssignedUserId,x.Status,x.DueAt});
            e.HasIndex(x=>new{x.PolicyId,x.PolicyOccurrenceAt}).IsUnique().HasFilter("policy_id IS NOT NULL");
        });
        b.Entity<FollowupHistory>(e=>{
            e.HasOne<Followup>().WithMany().HasForeignKey(x=>x.FollowupId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x=>x.Action).HasMaxLength(24);
            e.HasIndex(x=>new{x.FollowupId,x.CreatedAt});
        });
        b.Entity<CrmReminder>(e=>{
            e.Property(x=>x.EntityType).HasMaxLength(40);
            e.Property(x=>x.Channel).HasMaxLength(10);
            e.Property(x=>x.NotificationType).HasMaxLength(160);
            e.Property(x=>x.DedupeKey).HasMaxLength(200);
            e.Property(x=>x.Status).HasMaxLength(24);
            e.HasIndex(x=>x.DedupeKey).IsUnique();
            e.HasIndex(x=>new{x.Status,x.FireAt});
        });
        b.Entity<CrmOutboxMessage>(e=>{
            e.ToTable("crm_outbox_messages");
            e.HasKey(x=>x.Id);
            e.HasOne<CrmReminder>().WithMany().HasForeignKey(x=>x.ReminderId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x=>x.ReminderId).IsUnique();
            e.HasIndex(x=>new{x.Status,x.NextAttemptAt});
        });
        b.Entity<CrmInboxMessage>(e=>{
            e.ToTable("crm_inbox_messages");
            e.HasKey(x=>x.EventId);
            e.HasIndex(x=>x.ProcessedAt);
        });
    }
}
