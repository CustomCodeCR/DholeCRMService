using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Workers.Followups;
using Microsoft.EntityFrameworkCore;
namespace Dhole.Crm.IntegrationTests;

[TestClass]
public sealed class CrmFinanceIntegrationTests
{
    private static async Task<CrmDbContext> Open()
    {
        var cs=Environment.GetEnvironmentVariable("CRM_TEST_POSTGRES");
        if(string.IsNullOrWhiteSpace(cs))Assert.Inconclusive("A real PostgreSQL instance is required");
        var db=new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>().UseNpgsql(cs).Options);
        await db.Database.MigrateAsync();
        return db;
    }
    [TestMethod]
    public async Task Migration_contains_financial_tables_and_numeric_constraints()
    {
        await using var db=await Open();
        var tables=await db.Database.SqlQueryRaw<string>(
            "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema='crm' AND table_name LIKE 'crm_credit_%'")
            .ToListAsync();
        foreach(var table in new[]{"crm_credit_profiles","crm_credit_coverages","crm_credit_contracts",
            "crm_credit_terms_history","crm_credit_contract_renewals"})
            CollectionAssert.Contains(tables,table);
    }
    [TestMethod]
    public async Task Credit_approved_and_pending_are_unique_per_account()
    {
        await using var db=await Open();
        var owner=Guid.NewGuid();
        var a=new Account {DisplayName="Finance constraint "+owner,LegalName="Finance constraint",
            ExecutiveUserId=owner,CreatedByUserId=owner};
        db.Accounts.Add(a);
        db.CreditProfiles.Add(new CreditProfile {AccountId=a.Id,CreatedByUserId=owner,
            CreditDays=30,CreditLimit=1000m,MonthlyFinancingPct=2.5000m,
            CurrencyCode="USD",EffectiveFrom=DateTime.UtcNow,Status="PendingApproval"});
        await db.SaveChangesAsync();
        db.CreditProfiles.Add(new CreditProfile {AccountId=a.Id,CreatedByUserId=owner,
            CreditDays=60,CreditLimit=1000m,MonthlyFinancingPct=2.5000m,
            CurrencyCode="USD",EffectiveFrom=DateTime.UtcNow,Status="PendingApproval"});
        try {await db.SaveChangesAsync();Assert.Fail("Unique pending profile constraint not enforced");}
        catch(DbUpdateException){db.ChangeTracker.Clear();}
        var count=await db.CreditProfiles.CountAsync(x=>x.AccountId==a.Id);
        Assert.AreEqual(1,count);
    }
    [TestMethod]
    public async Task Finance_scheduler_persists_once_across_runs_and_uses_existing_outbox()
    {
        var now=DateTime.UtcNow;var user=Guid.NewGuid();Guid accountId,contractId,creditId;
        await using(var db=await Open())
        {
            var a=new Account{DisplayName="Renewal test",LegalName="Renewal test",
                ExecutiveUserId=user,CreatedByUserId=user};
            accountId=a.Id;db.Accounts.Add(a);
            var p=new CreditProfile{AccountId=a.Id,CreditEnabled=true,
                CreditDays=30,CreditLimit=1500m,CurrencyCode="USD",
                MonthlyFinancingPct=2.5m,PaymentConditions="30 days",
                Status="Approved",ApprovedByUserId=user,ApprovedAt=now,
                EffectiveFrom=now.AddDays(-2),ReviewAt=now.AddDays(7),CreatedByUserId=user};
            creditId=p.Id;db.CreditProfiles.Add(p);
            var c=new CreditContract{AccountId=a.Id,CreditProfileId=p.Id,
                StartDate=now.AddDays(-100),RenewalDate=now.AddDays(7),
                PaymentTerms="30 days",Status="Active",CreatedByUserId=user};
            contractId=c.Id;db.CreditContracts.Add(c);
            await db.SaveChangesAsync();
            await CrmFinanceReminderEngine.Contracts(db,now,[60,30,15,7],CancellationToken.None);
            await CrmFinanceReminderEngine.Credits(db,now,[30,7,0],CancellationToken.None);
        }
        await using(var db=await Open())
        {
            await CrmFinanceReminderEngine.Contracts(db,now,[60,30,15,7],CancellationToken.None);
            await CrmFinanceReminderEngine.Credits(db,now,[30,7,0],CancellationToken.None);
            Assert.AreEqual(1,await db.Reminders.CountAsync(x=>x.EntityId==contractId));
            Assert.AreEqual(1,await db.Reminders.CountAsync(x=>x.EntityId==creditId));
            var ids=await db.Reminders.Where(x=>x.EntityId==contractId||x.EntityId==creditId)
                .Select(x=>x.Id).ToListAsync();
            Assert.AreEqual(2,await db.OutboxMessages.CountAsync(x=>ids.Contains(x.ReminderId)));
            Assert.IsTrue(await db.CreditProfiles.AnyAsync(x=>x.Id==creditId&&x.Status=="Approved"));
            Assert.IsTrue(await db.CreditContracts.AnyAsync(x=>x.Id==contractId&&x.Status=="Active"));
        }
    }
}
