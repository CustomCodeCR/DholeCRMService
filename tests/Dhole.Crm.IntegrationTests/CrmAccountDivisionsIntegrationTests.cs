using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.IntegrationTests;

[TestClass]
public sealed class CrmAccountDivisionsIntegrationTests
{
    [TestMethod]
    public async Task Division_membership_cannot_cross_account_and_does_not_duplicate_contact_identity()
    {
        var connection = Environment.GetEnvironmentVariable("CRM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Assert.Inconclusive("CRM_TEST_POSTGRES is required");
            return;
        }
        await using var db = new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>()
            .UseNpgsql(connection).Options);
        await db.Database.MigrateAsync();
        var actor = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N");
        var a = new Account { DisplayName = "Division A " + suffix, LegalName = "A " + suffix,
            ExecutiveUserId = actor, CreatedByUserId = actor };
        var b = new Account { DisplayName = "Division B " + suffix, LegalName = "B " + suffix,
            ExecutiveUserId = actor, CreatedByUserId = actor };
        var person = new Contact { Name = "Operational " + suffix, CreatedByUserId = actor };
        var first = new AccountDivision { AccountId = a.Id, Name = "Purchasing", NormalizedName = "PURCHASING" };
        var second = new AccountDivision { AccountId = a.Id, Name = "Exports", NormalizedName = "EXPORTS" };
        var other = new AccountDivision { AccountId = b.Id, Name = "Purchasing", NormalizedName = "PURCHASING" };
        db.Accounts.AddRange(a, b);
        db.Contacts.Add(person);
        db.AccountContacts.Add(new AccountContact { AccountId = a.Id, ContactId = person.Id, IsOperationsContact = true });
        db.AddRange(first, second, other);
        db.AddRange(new AccountDivisionContact { AccountId = a.Id, DivisionId = first.Id, ContactId = person.Id },
            new AccountDivisionContact { AccountId = a.Id, DivisionId = second.Id, ContactId = person.Id });
        await db.SaveChangesAsync();
        Assert.AreEqual(2, await db.Set<AccountDivisionContact>().CountAsync(x => x.AccountId == a.Id && x.ContactId == person.Id));
        Assert.AreEqual(1, await db.Contacts.CountAsync(x => x.Id == person.Id));
        db.Add(new AccountDivisionContact { AccountId = a.Id, DivisionId = other.Id, ContactId = person.Id });
        await Assert.ThrowsExceptionAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
