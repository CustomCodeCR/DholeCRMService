using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.IntegrationTests;

[TestClass]
public sealed class CrmAccountGroupingIntegrationTests
{
    [TestMethod]
    public async Task Account_group_preserves_independent_fiscal_identity_and_existing_activities()
    {
        var connection = Environment.GetEnvironmentVariable("CRM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Assert.Inconclusive("CRM_TEST_POSTGRES is required for the real PostgreSQL migration test");
            return;
        }

        await using var db = new CrmDbContext(
            new DbContextOptionsBuilder<CrmDbContext>().UseNpgsql(connection).Options);
        await db.Database.MigrateAsync();

        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        CollectionAssert.Contains(applied, "20261112091000_AddAccountGrouping");

        var actor = Guid.NewGuid();
        var primary = new Account
        {
            DisplayName = "Group primary " + Guid.NewGuid().ToString("N"),
            LegalName = "Group primary legal identity",
            TaxId = "P-" + Guid.NewGuid().ToString("N"),
            ExecutiveUserId = actor,
            CreatedByUserId = actor
        };
        var subsidiary = new Account
        {
            DisplayName = "Group child " + Guid.NewGuid().ToString("N"),
            LegalName = "Separate subsidiary legal identity",
            TaxId = "S-" + Guid.NewGuid().ToString("N"),
            ParentAccountId = primary.Id,
            ExecutiveUserId = actor,
            CreatedByUserId = actor
        };
        var activity = new Activity
        {
            AccountId = subsidiary.Id,
            Subject = "Customer visit",
            Type = "VISIT",
            OwnerUserId = actor,
            CreatedByUserId = actor
        };
        db.Accounts.AddRange(primary, subsidiary);
        db.Activities.Add(activity);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var groupIds = await db.Accounts.AsNoTracking()
            .Where(account => account.Id == primary.Id || account.ParentAccountId == primary.Id)
            .Select(account => account.Id).ToArrayAsync();
        CollectionAssert.AreEquivalent(new[] { primary.Id, subsidiary.Id }, groupIds);
        Assert.AreEqual(1, await db.Activities.CountAsync(item =>
            item.AccountId.HasValue && groupIds.Contains(item.AccountId.Value)));

        var loadedChild = await db.Accounts.AsNoTracking()
            .SingleAsync(account => account.Id == subsidiary.Id);
        Assert.AreEqual(primary.Id, loadedChild.ParentAccountId);
        Assert.AreNotEqual(primary.TaxId, loadedChild.TaxId);
        Assert.AreNotEqual(primary.LegalName, loadedChild.LegalName);

        var changing = await db.Accounts.SingleAsync(account => account.Id == subsidiary.Id);
        changing.ParentAccountId = null;
        changing.RowVersion++;
        await db.SaveChangesAsync();
        Assert.AreEqual(0, await db.Accounts.AsNoTracking().CountAsync(account =>
            account.ParentAccountId == primary.Id));
        // No deletes or reassignments of history occur when leaving the group.
        Assert.AreEqual(subsidiary.Id, (await db.Activities.AsNoTracking()
            .SingleAsync(item => item.Id == activity.Id)).AccountId);
    }
}
