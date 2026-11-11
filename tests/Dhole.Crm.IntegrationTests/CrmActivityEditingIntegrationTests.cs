using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.IntegrationTests;

[TestClass]
public sealed class CrmActivityEditingIntegrationTests
{
    [TestMethod]
    public async Task Completed_activity_minutes_survive_an_edit_larger_than_legacy_limit()
    {
        var connection = Environment.GetEnvironmentVariable("CRM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection))
            Assert.Inconclusive("A real PostgreSQL database is mandatory for migration verification.");

        await using var db = new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>()
            .UseNpgsql(connection).Options);
        await db.Database.MigrateAsync();

        var owner = Guid.NewGuid();
        var account = new Account {
            DisplayName = "Activity editing test", LegalName = "Activity editing test",
            ExecutiveUserId = owner, CategoryCode = "D", CreatedByUserId = owner
        };
        var activity = new Activity {
            AccountId = account.Id, OwnerUserId = owner, Subject = "Completed meeting",
            Status = "Completed", Type = "MEETING", OccurredAt = DateTime.UtcNow,
            CreatedByUserId = owner
        };
        var first = new ActivityNote {
            ActivityId = activity.Id, AuthorUserId = owner, CreatedByUserId = owner,
            NoteText = "Minuta:\n" + new string('A', 9000)
        };
        db.Accounts.Add(account);
        db.Activities.Add(activity);
        db.ActivityNotes.Add(first);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var reloaded = await db.ActivityNotes.SingleAsync(x => x.Id == first.Id);
        Assert.AreEqual(first.NoteText, reloaded.NoteText);
        reloaded.NoteText = "Minuta editada:\n" + new string('B', 15000);
        reloaded.RowVersion++;
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        Assert.AreEqual("Minuta editada:\n" + new string('B', 15000),
            (await db.ActivityNotes.SingleAsync(x => x.Id == first.Id)).NoteText);
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        CollectionAssert.Contains(applied,
            "20261111160000_ExpandCrmActivityNotesForFullEditing");
    }
}
