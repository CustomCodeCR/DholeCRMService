using System.Data;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Workers.Followups;

// A single short-lived PostgreSQL transaction acts as the cluster-wide scheduler lease.
// Every generated occurrence and reminder has a unique durable identity.
public static class CrmReminderEngine
{
    public static async Task<bool> Acquire(CrmDbContext db,long key,CancellationToken ct) =>
        await db.Database.SqlQuery<bool>(
            $"SELECT pg_try_advisory_xact_lock({key}) AS \"Value\"").SingleAsync(ct);

    public static async Task GenerateAsync(CrmDbContext db,DateTime now,
        CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        if(!await Acquire(db,5012505,ct))return;
        var policies=await db.AccountFollowupPolicies
            .Where(x=>x.Enabled && x.NextDueAt<=now &&
                (x.PausedUntil==null||x.PausedUntil<=now))
            .OrderBy(x=>x.NextDueAt).Take(100).ToListAsync(ct);
        foreach(var p in policies)
        {
            if(!await db.Accounts.AsNoTracking().AnyAsync(x=>x.Id==p.AccountId&&x.ArchivedAt==null,ct))
                continue;
            var due=p.NextDueAt;
            if(!await db.Followups.AnyAsync(x=>x.PolicyId==p.Id&&x.PolicyOccurrenceAt==due,ct))
            {
                var followup=new Followup {AccountId=p.AccountId,PolicyId=p.Id,
                    PolicyOccurrenceAt=due,AssignedUserId=p.OwnerUserId,
                    Channel=p.ChannelStrategy=="EMAIL"?"EMAIL":"SYSTEM",DueAt=due,
                    CreatedByUserId=p.CreatedByUserId};
                db.Followups.Add(followup);
                db.FollowupHistories.Add(new FollowupHistory {FollowupId=followup.Id,
                    Action="Created",ActorUserId=p.OwnerUserId,Reason="Policy scheduler",
                    NewDueAt=due,CreatedByUserId=p.CreatedByUserId});
            }
            // Preserve the account's local wall-clock hour through DST boundaries.
            p.NextDueAt=NextOccurrence(due,p.FrequencyDays,p.TimezoneId,now);
            p.RowVersion++;p.UpdatedAt=now;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public static DateTime NextOccurrence(DateTime previous,int frequencyDays,
        string timezone,DateTime now)
    {
        var zone=TimeZoneInfo.FindSystemTimeZoneById(timezone);
        var local=TimeZoneInfo.ConvertTimeFromUtc(previous,zone);
        var candidate=DateTime.SpecifyKind(local.AddDays(frequencyDays),DateTimeKind.Unspecified);
        while(true)
        {
            if(zone.IsInvalidTime(candidate))candidate=candidate.AddHours(1);
            var utc=TimeZoneInfo.ConvertTimeToUtc(candidate,zone);
            if(utc>now)return utc;
            candidate=candidate.AddDays(frequencyDays);
        }
    }

    public static async Task PlanRemindersAsync(CrmDbContext db,DateTime now,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        if(!await Acquire(db,5012506,ct))return;
        var future=now.AddHours(24);
        var active=await db.Followups
            .Where(x=>(x.Status=="Pending"||x.Status=="Overdue") && x.DueAt<=future)
            .OrderBy(x=>x.DueAt).Take(200).ToListAsync(ct);
        foreach(var f in active)
        {
            var p=f.PolicyId is { } pid
                ? await db.AccountFollowupPolicies.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==pid,ct)
                : null;
            if(p is { Enabled:false } || p?.PausedUntil>now)continue;
            var lead=p?.ReminderLeadMinutes??60;
            var channel=p?.ChannelStrategy??"BOTH";
            if(f.DueAt<now && f.Status=="Pending")
            {
                f.Status="Overdue";f.RowVersion++;f.UpdatedAt=now;
                db.FollowupHistories.Add(new FollowupHistory {
                    FollowupId=f.Id,Action="Overdue",ActorUserId=Guid.Empty,
                    Reason="Past due; execution was not assumed",CreatedByUserId=f.CreatedByUserId
                });
            }
            await AddReminders(db,"Followup",f.Id,f.AssignedUserId,f.RowVersion,f.DueAt,
                now,lead,channel,"crm.followup.reminder.v1",f.CreatedByUserId,ct);
        }
        var visits=await db.Activities.AsNoTracking()
            .Where(x=>x.ArchivedAt==null && x.Status=="Scheduled" &&
                (x.Type=="VISIT"||x.Type=="MEETING") && x.ScheduledStartAt!=null &&
                x.ScheduledStartAt<=future && x.ScheduledStartAt>now.AddHours(-24))
            .OrderBy(x=>x.ScheduledStartAt).Take(200).ToListAsync(ct);
        foreach(var v in visits)
        {
            var p=v.AccountId is { } account
                ? await db.AccountFollowupPolicies.AsNoTracking().FirstOrDefaultAsync(x=>x.AccountId==account,ct)
                : null;
            if(p is {Enabled:false} || p?.PausedUntil>now)continue;
            await AddReminders(db,"Activity",v.Id,v.OwnerUserId,v.RowVersion,v.ScheduledStartAt!.Value,
                now,p?.ReminderLeadMinutes??60,p?.ChannelStrategy??"BOTH",
                "crm.visit.reminder.v1",v.CreatedByUserId,ct);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
    private static async Task AddReminders(CrmDbContext db,string entityType,Guid entityId,
        Guid userId,long version,DateTime due,DateTime now,int leadMinutes,
        string strategy,string type,Guid createdBy,CancellationToken ct)
    {
        // No customer-directed notifications: these reminders are addressed to the owner only.
        foreach(var channel in strategy switch
        {
            "EMAIL"=>new[]{"Email"},
            "SYSTEM"=>new[]{"System"},
            _=>new[]{"Email","System"}
        })
        {
            var key=$"{entityType}:{entityId:N}:{version}:{channel}";
            // Re-read dedupe across restarts; PostgreSQL unique index is the final arbiter.
            if(db.Reminders.Local.Any(x=>x.DedupeKey==key) ||
               await db.Reminders.AsNoTracking().AnyAsync(x=>x.DedupeKey==key,ct))continue;
            // Existence tested by caller's transaction before adding.
            var reminder=new CrmReminder {
                EntityType=entityType,EntityId=entityId,RecipientUserId=userId,Channel=channel,
                NotificationType=type,DedupeKey=key,TargetVersion=version,
                FireAt=due.AddMinutes(-leadMinutes),CreatedByUserId=createdBy
            };
            db.Reminders.Add(reminder);
            db.OutboxMessages.Add(new CrmOutboxMessage {Id=reminder.Id,ReminderId=reminder.Id,
                NextAttemptAt=reminder.FireAt>now?reminder.FireAt:now});
        }
    }

    public static async Task MarkOverdueActivitiesAsync(CrmDbContext db,DateTime now,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        if(!await Acquire(db,5012507,ct))return;
        // No fabricated completion: create a once-only overdue reminder for the assigned seller.
        var overdue=await db.Activities.AsNoTracking()
            .Where(x=>x.Status=="Scheduled"&&x.ArchivedAt==null&&
                x.ScheduledEndAt!=null&&x.ScheduledEndAt<now&&x.ScheduledEndAt>now.AddDays(-30))
            .OrderBy(x=>x.ScheduledEndAt).Take(100).ToListAsync(ct);
        foreach(var a in overdue)
        {
            var key=$"overdue:{a.Id:N}:{a.RowVersion}:System";
            if(await db.Reminders.AnyAsync(x=>x.DedupeKey==key,ct))continue;
            var rem=new CrmReminder {EntityType="Activity",EntityId=a.Id,RecipientUserId=a.OwnerUserId,
                Channel="System",NotificationType="crm.activity.overdue.v1",DedupeKey=key,
                FireAt=now,TargetVersion=a.RowVersion,CreatedByUserId=a.CreatedByUserId};
            db.Reminders.Add(rem);
            db.OutboxMessages.Add(new CrmOutboxMessage {Id=rem.Id,ReminderId=rem.Id,NextAttemptAt=now});
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
