using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Dhole.Crm.Api.Endpoints;
// Persist advance and exact-start reminder intent together with the activity.
internal static class CrmReminderEnqueue
{
    internal static async Task QueueAsync(CrmDbContext db, string entityType, Guid entityId,
        Guid? accountId, Guid recipientUserId, long targetVersion, DateTime dueAt,
        int? leadMinutes, string? channelStrategy, string notificationType,
        Guid createdByUserId, CancellationToken ct)
    {
        var policy=accountId.HasValue
            ? await db.AccountFollowupPolicies.AsNoTracking().FirstOrDefaultAsync(x=>x.AccountId==accountId.Value,ct)
            : null;
        if(policy is { Enabled:false } || policy?.PausedUntil>DateTime.UtcNow)return;
        var advanceMinutes=Math.Clamp(leadMinutes??policy?.ReminderLeadMinutes??60,0,10080);
        var strategy=entityType is "Activity" or "Followup"
            ? "BOTH" : channelStrategy??policy?.ChannelStrategy??"BOTH";
        var now=DateTime.UtcNow;
        var stages=advanceMinutes>0 && (entityType is "Activity" or "Followup")
            ? new[] {(Phase:"lead",FireAt:dueAt.AddMinutes(-advanceMinutes),Type:notificationType),
                     (Phase:"start",FireAt:dueAt,Type:"crm.commitment.start.v1")}
            : new[] {(Phase:"lead",FireAt:dueAt.AddMinutes(-advanceMinutes),Type:notificationType)};
        foreach(var channel in strategy switch
        {
            "EMAIL"=>new[]{"Email"},
            "SYSTEM"=>new[]{"System"},
            _=>new[]{"Email","System"}
        })
        foreach(var stage in stages)
        {
            var key=stage.Phase=="lead"
                ? $"{entityType}:{entityId:N}:{targetVersion}:{channel}"
                : $"crmcommit:{entityType}:{entityId:N}:{targetVersion}:start:owner:{channel}";
            if(db.Reminders.Local.Any(x=>x.DedupeKey==key) ||
                await db.Reminders.AsNoTracking().AnyAsync(x=>x.DedupeKey==key,ct))continue;
            var reminder=new CrmReminder
            {
                EntityType=entityType,EntityId=entityId,RecipientUserId=recipientUserId,
                Channel=channel,NotificationType=stage.Type,DedupeKey=key,
                TargetVersion=targetVersion,FireAt=stage.FireAt,CreatedByUserId=createdByUserId
            };
            db.Reminders.Add(reminder);
            db.OutboxMessages.Add(new CrmOutboxMessage
            {
                Id=reminder.Id,ReminderId=reminder.Id,
                NextAttemptAt=stage.FireAt>now?stage.FireAt:now
            });
        }
    }
}
