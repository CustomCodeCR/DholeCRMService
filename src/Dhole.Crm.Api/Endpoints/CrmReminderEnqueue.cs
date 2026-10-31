using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

// Persist reminder intent and its notification outbox entry in the same transaction
// as the activity/followup. This also covers appointments created after their
// configured reminder time (e.g. a 5-minute warning set 2 minutes in advance).
internal static class CrmReminderEnqueue
{
    internal static async Task QueueAsync(CrmDbContext db, string entityType, Guid entityId,
        Guid? accountId, Guid recipientUserId, long targetVersion, DateTime dueAt,
        int? leadMinutes, string? channelStrategy, string notificationType,
        Guid createdByUserId, CancellationToken ct)
    {
        var policy = accountId.HasValue
            ? await db.AccountFollowupPolicies.AsNoTracking()
                .FirstOrDefaultAsync(x => x.AccountId == accountId.Value, ct)
            : null;
        if (policy is { Enabled: false } || policy?.PausedUntil > DateTime.UtcNow)
            return;

        var advanceMinutes = Math.Clamp(leadMinutes ?? policy?.ReminderLeadMinutes ?? 60, 0, 10080);
        var strategy = channelStrategy ?? policy?.ChannelStrategy ?? "BOTH";
        var now = DateTime.UtcNow;

        foreach (var channel in strategy switch
        {
            "EMAIL" => new[] { "Email" },
            "SYSTEM" => new[] { "System" },
            _ => new[] { "Email", "System" }
        })
        {
            var key = $"{entityType}:{entityId:N}:{targetVersion}:{channel}";
            if (db.Reminders.Local.Any(x => x.DedupeKey == key) ||
                await db.Reminders.AsNoTracking().AnyAsync(x => x.DedupeKey == key, ct))
                continue;

            var fireAt = dueAt.AddMinutes(-advanceMinutes);
            var reminder = new CrmReminder
            {
                EntityType = entityType,
                EntityId = entityId,
                RecipientUserId = recipientUserId,
                Channel = channel,
                NotificationType = notificationType,
                DedupeKey = key,
                TargetVersion = targetVersion,
                FireAt = fireAt,
                CreatedByUserId = createdByUserId
            };
            db.Reminders.Add(reminder);
            db.OutboxMessages.Add(new CrmOutboxMessage
            {
                Id = reminder.Id,
                ReminderId = reminder.Id,
                NextAttemptAt = fireAt > now ? fireAt : now
            });
        }
    }
}
