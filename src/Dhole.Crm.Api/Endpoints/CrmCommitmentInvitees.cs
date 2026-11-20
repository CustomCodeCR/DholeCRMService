using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

/// <summary>
/// Keep a dated agreement's shared agenda membership in sync with the parent
/// activity at creation time. Memberships and notification outbox records are
/// saved by the caller together with the followup in a single EF transaction.
/// </summary>
public static class CrmCommitmentInvitees
{
    public static async Task AttachAsync(CrmDbContext db, Followup followup,
        IEnumerable<Guid> activityInvitedUserIds, Guid actor, CancellationToken ct)
    {
        foreach (var userId in activityInvitedUserIds
            .Where(id => id != Guid.Empty && id != followup.AssignedUserId)
            .Distinct())
        {
            if (db.FollowupInvitees.Local.Any(x =>
                    x.FollowupId == followup.Id && x.UserId == userId) ||
                await db.FollowupInvitees.AsNoTracking().AnyAsync(x =>
                    x.FollowupId == followup.Id && x.UserId == userId, ct))
                continue;

            var membership = new FollowupInvitee
            {
                FollowupId = followup.Id,
                UserId = userId,
                CreatedByUserId = actor
            };
            db.FollowupInvitees.Add(membership);
            db.FollowupHistories.Add(new FollowupHistory
            {
                FollowupId = followup.Id, ActorUserId = actor,
                Action = "Invited", Reason = "Usuario invitado de actividad principal: " + userId,
                CreatedByUserId = actor
            });

            // Historical or completed commitments retain memberships but must
            // never produce retroactive notifications.
            if (followup.Status is not ("Pending" or "Overdue") ||
                followup.DueAt <= DateTime.UtcNow) continue;

            await CrmReminderEnqueue.QueueInvitationAsync(db, "Followup",
                followup.Id, membership.Id, userId, followup.RowVersion, actor, ct);
            await CrmReminderEnqueue.QueueAsync(db, "Followup", followup.Id,
                followup.AccountId, userId, followup.RowVersion, followup.DueAt,
                followup.ReminderLeadMinutes, followup.ReminderChannelStrategy,
                "crm.followup.reminder.v1", actor, ct, isInvitee: true);
        }
    }
}
