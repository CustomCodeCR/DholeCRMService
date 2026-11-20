using Dhole.Crm.Api.Endpoints;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.IntegrationTests;

[TestClass]
public sealed class CrmCommitmentInviteesIntegrationTests
{
    private static async Task<CrmDbContext> Open()
    {
        var connection = Environment.GetEnvironmentVariable("CRM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection))
            Assert.Inconclusive("Real PostgreSQL is required for CRM invitation persistence.");
        var db = new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>()
            .UseNpgsql(connection).Options);
        await db.Database.MigrateAsync();
        return db;
    }

    private static Account AccountFor(Guid owner) => new()
    {
        DisplayName = "Cuenta para invitaciones", LegalName = "Cuenta para invitaciones",
        ExecutiveUserId = owner, CategoryType = "CUSTOMER", CategoryCode = "D",
        CreatedByUserId = owner
    };

    [TestMethod]
    public async Task Agreement_followup_persists_two_distinct_invitees_and_durable_outbox()
    {
        await using var db = await Open();
        var owner = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var account = AccountFor(owner);
        db.Accounts.Add(account);
        var activity = new Activity
        {
            AccountId = account.Id, OwnerUserId = owner, Type = "COMMERCIAL_FOLLOWUP",
            Subject = "Actividad original", Status = "Scheduled",
            ScheduledStartAt = DateTime.UtcNow.AddHours(1),
            ScheduledEndAt = DateTime.UtcNow.AddHours(2),
            CreatedByUserId = owner
        };
        var commitment = new Followup
        {
            AccountId = account.Id, OriginActivityId = activity.Id, AssignedUserId = owner,
            Subject = "Acuerdo de la actividad", DueAt = DateTime.UtcNow.AddHours(4),
            ReminderLeadMinutes = 60, ReminderChannelStrategy = "BOTH",
            CreatedByUserId = owner
        };
        db.Activities.Add(activity);
        db.Followups.Add(commitment);
        db.ActivityParticipants.AddRange(
            new ActivityParticipant { ActivityId = activity.Id, UserId = first,
                Role = "RESPONSIBLE", CreatedByUserId = owner },
            new ActivityParticipant { ActivityId = activity.Id, UserId = second,
                Role = "RESPONSIBLE", CreatedByUserId = owner });

        await CrmCommitmentInvitees.AttachAsync(db, commitment,
            [first, second, first, owner], owner, CancellationToken.None);
        await CrmCommitmentInvitees.AttachAsync(db, commitment,
            [first, second], owner, CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var stored = await db.FollowupInvitees.AsNoTracking()
            .Where(x => x.FollowupId == commitment.Id)
            .Select(x => x.UserId).ToListAsync();
        CollectionAssert.AreEquivalent(new[] { first, second }, stored);
        Assert.AreEqual(2, await db.FollowupHistories.CountAsync(x =>
            x.FollowupId == commitment.Id && x.Action == "Invited"));
        Assert.AreEqual(2, await db.Reminders.CountAsync(x =>
            x.EntityType == "Followup" && x.EntityId == commitment.Id &&
            x.NotificationType == "crm.followup.invited.v1"));
        Assert.AreEqual(4, await db.Reminders.CountAsync(x =>
            x.EntityType == "Followup" && x.EntityId == commitment.Id &&
            (x.RecipientUserId == first || x.RecipientUserId == second) &&
            x.NotificationType == "crm.followup.reminder.v1"));
        var reminders = await db.Reminders.AsNoTracking()
            .Where(x => x.EntityType == "Followup" && x.EntityId == commitment.Id)
            .Select(x => x.Id).ToListAsync();
        Assert.AreEqual(reminders.Count, await db.OutboxMessages
            .CountAsync(x => reminders.Contains(x.ReminderId)));
    }

    [TestMethod]
    public async Task Completed_agreement_retains_membership_without_retroactive_alerts()
    {
        await using var db = await Open();
        var owner = Guid.NewGuid();
        var guest = Guid.NewGuid();
        var account = AccountFor(owner);
        db.Accounts.Add(account);
        var activity = new Activity
        {
            AccountId = account.Id, OwnerUserId = owner, Type = "CALL", Subject = "Bitácora histórica",
            Status = "Completed", OccurredAt = DateTime.UtcNow,
            CreatedByUserId = owner
        };
        var commitment = new Followup
        {
            AccountId = account.Id, OriginActivityId = activity.Id, AssignedUserId = owner,
            Subject = "Acuerdo cerrado", DueAt = DateTime.UtcNow.AddHours(-1),
            Status = "Completed", CompletedAt = DateTime.UtcNow,
            CreatedByUserId = owner
        };
        db.Activities.Add(activity);
        db.Followups.Add(commitment);
        await CrmCommitmentInvitees.AttachAsync(db, commitment, [guest], owner, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.AreEqual(1, await db.FollowupInvitees.CountAsync(x =>
            x.FollowupId == commitment.Id && x.UserId == guest));
        Assert.AreEqual(0, await db.Reminders.CountAsync(x =>
            x.EntityType == "Followup" && x.EntityId == commitment.Id));
    }
}
