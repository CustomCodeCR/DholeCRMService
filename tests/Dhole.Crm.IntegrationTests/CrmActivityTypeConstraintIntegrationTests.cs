using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.IntegrationTests;

[TestClass]
public sealed class CrmActivityTypeConstraintIntegrationTests
{
    [TestMethod]
    public async Task Real_postgres_accepts_all_active_commercial_activity_types_as_unscheduled_log_entries()
    {
        var connection = Environment.GetEnvironmentVariable("CRM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Assert.Inconclusive("PostgreSQL integration testing requires CRM_TEST_POSTGRES.");
            return;
        }

        await using var db = new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>()
            .UseNpgsql(connection).Options);
        await db.Database.MigrateAsync();
        CollectionAssert.Contains((await db.Database.GetAppliedMigrationsAsync()).ToList(),
            "20261113043000_AllowExtendedCrmActivityTypes");

        var owner = Guid.NewGuid();
        var account = new Account
        {
            DisplayName = "Activity type constraint test",
            LegalName = "Activity type constraint test",
            ExecutiveUserId = owner,
            CategoryCode = "D",
            CreatedByUserId = owner
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var codes = new[]
        {
            "VISIT", "CALL", "EMAIL", "MEETING", "OTHER",
            "COMMERCIAL_FOLLOWUP", "WHATSAPP", "RECOMMENDATION", "OFFER"
        };
        foreach (var code in codes)
            db.Activities.Add(new Activity
            {
                AccountId = account.Id, OwnerUserId = owner,
                Type = code, Subject = "Historical commercial note " + code,
                Status = "Completed", OccurredAt = DateTime.UtcNow,
                Outcome = "COMPLETED", CreatedByUserId = owner
            });

        await db.SaveChangesAsync();
        var saved = await db.Activities.AsNoTracking()
            .Where(x => x.AccountId == account.Id).ToListAsync();
        CollectionAssert.AreEquivalent(codes, saved.Select(x => x.Type).ToArray());
        Assert.IsTrue(saved.All(x => x.Status == "Completed"
            && x.ScheduledStartAt is null && x.ScheduledEndAt is null
            && x.OccurredAt.HasValue), "Log entries must not require a schedule.");

        db.Activities.Add(new Activity
        {
            AccountId = account.Id, OwnerUserId = owner,
            Type = "UNKNOWN", Subject = "Invalid type must be rejected",
            Status = "Completed", OccurredAt = DateTime.UtcNow,
            CreatedByUserId = owner
        });
        var invalidWasRejected = false;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { invalidWasRejected = true; }
        Assert.IsTrue(invalidWasRejected,
            "PostgreSQL must still reject unknown activity codes.");
    }
}
