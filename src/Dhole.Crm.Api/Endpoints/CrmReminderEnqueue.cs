using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Dhole.Crm.Api.Endpoints;
// Persist advance and exact-start reminder intent together with the activity.
internal static class CrmReminderEnqueue
{
    // Invitation notifications are immediate, durable, idempotent per membership ID.
    // They reuse the same NotificationService + SignalR pipeline as CRM reminders.
    internal static async Task QueueInvitationAsync(CrmDbContext db,string entityType,
        Guid entityId,Guid membershipId,Guid recipientUserId,long targetVersion,
        Guid actorUserId,CancellationToken ct)
    {
        var key=$"crm-invite:{entityType}:{entityId:N}:{membershipId:N}:invitee:{recipientUserId:N}";
        if(db.Reminders.Local.Any(x=>x.DedupeKey==key) ||
           await db.Reminders.AsNoTracking().AnyAsync(x=>x.DedupeKey==key,ct))return;
        var now=DateTime.UtcNow;
        var reminder=new CrmReminder {
            EntityType=entityType,EntityId=entityId,RecipientUserId=recipientUserId,
            Channel="System",NotificationType=entityType=="Activity"
                ?"crm.activity.invited.v1":"crm.followup.invited.v1",
            DedupeKey=key,TargetVersion=targetVersion,FireAt=now,
            CreatedByUserId=actorUserId
        };
        db.Reminders.Add(reminder);
        db.OutboxMessages.Add(new CrmOutboxMessage {
            Id=reminder.Id,ReminderId=reminder.Id,NextAttemptAt=now
        });
    }
    internal static async Task QueueAsync(CrmDbContext db, string entityType, Guid entityId,
        Guid? accountId, Guid recipientUserId, long targetVersion, DateTime dueAt,
        int? leadMinutes, string? channelStrategy, string notificationType,
        Guid createdByUserId, CancellationToken ct, bool isInvitee = false)
    {
        var policy=accountId.HasValue
            ? await db.AccountFollowupPolicies.AsNoTracking().FirstOrDefaultAsync(x=>x.AccountId==accountId.Value,ct)
            : null;
        if(policy is { Enabled:false } || policy?.PausedUntil>DateTime.UtcNow)return;
        var advanceMinutes=Math.Clamp(leadMinutes??policy?.ReminderLeadMinutes??60,0,10080);
        var strategy=entityType=="Activity" ? "BOTH"
            : channelStrategy??policy?.ChannelStrategy??"BOTH";
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
            // Owner identifiers retain their historical dedupe keys.
            // Invitees receive separate, idempotent reminders per user and phase.
            var key=stage.Phase=="lead"
                ? isInvitee
                    ? $"{entityType}:{entityId:N}:{targetVersion}:invitee:{recipientUserId:N}:{channel}"
                    : $"{entityType}:{entityId:N}:{targetVersion}:{channel}"
                : $"crmcommit:{entityType}:{entityId:N}:{targetVersion}:start:"+
                    (isInvitee?$"invitee:{recipientUserId:N}":"owner")+ $":{channel}";
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
