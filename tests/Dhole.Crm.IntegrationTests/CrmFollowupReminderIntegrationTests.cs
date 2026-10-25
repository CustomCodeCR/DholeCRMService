using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.IntegrationTests;
[TestClass]
public sealed class CrmFollowupReminderIntegrationTests
{
    private static async Task<CrmDbContext> Open()
    {
        var cs=Environment.GetEnvironmentVariable("CRM_TEST_POSTGRES");
        if(string.IsNullOrWhiteSpace(cs))Assert.Inconclusive("Real PostgreSQL required");
        var db=new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>().UseNpgsql(cs).Options);
        await db.Database.MigrateAsync();
        return db;
    }
    [TestMethod]
    public async Task Phase5_migration_creates_policies_followups_history_reminders_outbox_inbox()
    {
        await using var db=await Open();
        var tables=await db.Database.SqlQueryRaw<string>(
            "SELECT table_name::text FROM information_schema.tables WHERE table_schema='crm'").ToListAsync();
        foreach(var name in new[]{"crm_account_followup_policies","crm_followups",
            "crm_followup_history","crm_reminders","crm_outbox_messages","crm_inbox_messages"})
            CollectionAssert.Contains(tables,name);
        var migrations=(await db.Database.GetAppliedMigrationsAsync()).ToList();
        CollectionAssert.Contains(migrations,"20261025150000_AddFollowupsRemindersMessaging");
    }
    [TestMethod]
    public async Task Repeated_policy_occurrence_cannot_create_duplicate_followup()
    {
        await using var db=await Open();
        var user=Guid.NewGuid();var date=DateTime.UtcNow.AddHours(2);
        var account=new Account {DisplayName="Policy account",LegalName="Policy account",
            ExecutiveUserId=user,CreatedByUserId=user,CategoryType="CUSTOMER",CategoryCode="D"};
        var p=new AccountFollowupPolicy {AccountId=account.Id,OwnerUserId=user,
            NextDueAt=date,CreatedByUserId=user};
        db.Accounts.Add(account);db.AccountFollowupPolicies.Add(p);
        db.Followups.Add(new Followup {AccountId=account.Id,PolicyId=p.Id,PolicyOccurrenceAt=date,
            AssignedUserId=user,DueAt=date,CreatedByUserId=user});
        await db.SaveChangesAsync();
        db.Followups.Add(new Followup {AccountId=account.Id,PolicyId=p.Id,PolicyOccurrenceAt=date,
            AssignedUserId=user,DueAt=date,CreatedByUserId=user});
        var rejected=false;
        try{await db.SaveChangesAsync();}
        catch(DbUpdateException){rejected=true;}
        Assert.IsTrue(rejected,"PostgreSQL must prevent two followups per policy occurrence");
    }
    [TestMethod]
    public async Task Reminder_and_outbox_are_atomic_and_deduped_after_restart()
    {
        var cs=Environment.GetEnvironmentVariable("CRM_TEST_POSTGRES");
        if(string.IsNullOrWhiteSpace(cs))Assert.Inconclusive("Real PostgreSQL required");
        var user=Guid.NewGuid();var id=Guid.NewGuid();
        await using(var db=await Open())
        {
            var r=new CrmReminder {Id=id,EntityType="Activity",EntityId=Guid.NewGuid(),
                RecipientUserId=user,Channel="System",NotificationType="crm.visit.reminder.v1",
                FireAt=DateTime.UtcNow,DedupeKey="activity-"+id,CreatedByUserId=user};
            db.Reminders.Add(r);
            db.OutboxMessages.Add(new CrmOutboxMessage {Id=id,ReminderId=id});
            await db.SaveChangesAsync();
        }
        await using(var db=new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>().UseNpgsql(cs).Options))
        {
            Assert.AreEqual(1,await db.OutboxMessages.CountAsync(x=>x.ReminderId==id));
            Assert.AreEqual(1,await db.Reminders.CountAsync(x=>x.Id==id));
            db.Reminders.Add(new CrmReminder {EntityType="Activity",EntityId=Guid.NewGuid(),
                RecipientUserId=user,Channel="System",NotificationType="crm.visit.reminder.v1",
                FireAt=DateTime.UtcNow,DedupeKey="activity-"+id,CreatedByUserId=user});
            var rejected=false;
            try{await db.SaveChangesAsync();}
            catch(DbUpdateException){rejected=true;}
            Assert.IsTrue(rejected,"A repeated notification key must not enqueue a second reminder");
        }
    }
    [TestMethod]
    public async Task Followup_must_have_subject_and_completion_is_consistent()
    {
        await using var db=await Open();
        var user=Guid.NewGuid();
        db.Followups.Add(new Followup {AssignedUserId=user,DueAt=DateTime.UtcNow,
            CreatedByUserId=user,Status="Completed"});
        var rejected=false;
        try{await db.SaveChangesAsync();}
        catch(DbUpdateException){rejected=true;}
        Assert.IsTrue(rejected,"SQL prevents misleading completed status or missing subject");
    }
    [TestMethod]
    public async Task Inbox_event_ids_are_persistent_and_unique()
    {
        await using var db=await Open();
        var id=Guid.NewGuid();
        db.InboxMessages.Add(new CrmInboxMessage {EventId=id,EventName="pricing.rate-header.updated"});
        await db.SaveChangesAsync();
        db.InboxMessages.Add(new CrmInboxMessage {EventId=id,EventName="pricing.rate-header.updated"});
        var rejected=false;
        try{await db.SaveChangesAsync();}
        catch(DbUpdateException){rejected=true;}
        Assert.IsTrue(rejected,"Inbox must deduplicate after restarts");
    }
}
