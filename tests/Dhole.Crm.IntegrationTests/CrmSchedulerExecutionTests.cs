using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Workers.Followups;
using Microsoft.EntityFrameworkCore;
namespace Dhole.Crm.IntegrationTests;
[TestClass]
public sealed class CrmSchedulerExecutionTests
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
    public async Task Scheduler_generates_each_policy_occurrence_exactly_once_across_restarts()
    {
        var user=Guid.NewGuid();var now=DateTime.UtcNow;
        Guid id;
        await using(var db=await Open())
        {
            var account=new Account {DisplayName="Scheduler account",LegalName="Scheduler account",
                ExecutiveUserId=user,CreatedByUserId=user};
            id=account.Id;
            db.Accounts.Add(account);
            db.AccountFollowupPolicies.Add(new AccountFollowupPolicy {
                AccountId=account.Id,OwnerUserId=user,ChannelStrategy="BOTH",FrequencyDays=3,
                NextDueAt=now.AddMinutes(-10),ReminderLeadMinutes=60,
                TimezoneId="America/Costa_Rica",CreatedByUserId=user
            });
            await db.SaveChangesAsync();
            await CrmReminderEngine.GenerateAsync(db,now,CancellationToken.None);
        }
        await using(var db=await Open())
        {
            await CrmReminderEngine.GenerateAsync(db,now,CancellationToken.None);
            var policy=await db.AccountFollowupPolicies.SingleAsync(x=>x.AccountId==id);
            Assert.IsTrue(policy.NextDueAt>now);
            Assert.AreEqual(1,await db.Followups.CountAsync(x=>x.PolicyId==policy.Id));
            await CrmReminderEngine.PlanRemindersAsync(db,now,CancellationToken.None);
            var followup=await db.Followups.SingleAsync(x=>x.PolicyId==policy.Id);
            var reminders=await db.Reminders.Where(x=>x.EntityId==followup.Id).ToListAsync();
            Assert.HasCount(2,reminders);
            Assert.IsTrue(reminders.All(x=>x.TargetVersion==followup.RowVersion));
            Assert.AreEqual(2,await db.OutboxMessages.CountAsync(x=>reminders.Select(r=>r.Id).Contains(x.ReminderId)));
        }
        await using(var db=await Open())
        {
            await CrmReminderEngine.PlanRemindersAsync(db,now,CancellationToken.None);
            var followup=await db.Followups.SingleAsync(x=>x.AccountId==id);
            Assert.AreEqual(2,await db.Reminders.CountAsync(x=>x.EntityId==followup.Id));
        }
    }
    [TestMethod]
    public async Task Visit_scheduler_does_not_double_enqueue_or_mark_visit_completed()
    {
        var user=Guid.NewGuid();var now=DateTime.UtcNow;var lead=new Lead {
            Name="Visita de prueba",ExecutiveUserId=user,CreatedByUserId=user};
        var visit=new Activity {LeadId=lead.Id,OwnerUserId=user,Type="VISIT",
            Subject="Customer visit",ScheduledStartAt=now.AddMinutes(15),
            ScheduledEndAt=now.AddMinutes(45),CreatedByUserId=user};
        await using(var db=await Open())
        {
            db.Leads.Add(lead);db.Activities.Add(visit);await db.SaveChangesAsync();
            await CrmReminderEngine.PlanRemindersAsync(db,now,CancellationToken.None);
        }
        await using(var db=await Open())
        {
            await CrmReminderEngine.PlanRemindersAsync(db,now,CancellationToken.None);
            Assert.AreEqual(2,await db.Reminders.CountAsync(x=>x.EntityId==visit.Id));
            Assert.AreEqual("Scheduled",(await db.Activities.SingleAsync(x=>x.Id==visit.Id)).Status);
        }
    }
    [TestMethod]
    public void Timezone_recurrence_keeps_client_wall_clock_not_server_time()
    {
        var prior=new DateTime(2026,10,25,14,0,0,DateTimeKind.Utc);
        var after=CrmReminderEngine.NextOccurrence(prior,7,"America/Costa_Rica",prior);
        Assert.AreEqual(prior.AddDays(7),after);
    }
}
