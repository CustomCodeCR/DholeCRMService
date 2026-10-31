using System.Data;
using CustomCodeFramework.Redis.Streams.Abstractions;
using CustomCodeFramework.Redis.Streams.Messages;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Workers.Followups;

internal sealed class CrmFollowupSchedulerWorker(IServiceScopeFactory scopes,
    IConfiguration configuration,ILogger<CrmFollowupSchedulerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var interval=new PeriodicTimer(TimeSpan.FromSeconds(
            Math.Clamp(configuration.GetValue("Crm:Scheduler:IntervalSeconds",300),30,3600)));
        do
        {
            if(configuration.GetValue("Features:CrmBackgroundTasksEnabled",false))
            {
                try
                {
                    await using var scope=scopes.CreateAsyncScope();
                    var db=scope.ServiceProvider.GetRequiredService<CrmDbContext>();
                    var now=DateTime.UtcNow;
                    await CrmReminderEngine.GenerateAsync(db,now,stoppingToken);
                    await CrmReminderEngine.PlanRemindersAsync(db,now,stoppingToken);
                    await CrmReminderEngine.MarkOverdueActivitiesAsync(db,now,stoppingToken);
                }
                catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
                catch(Exception ex){logger.LogError(ex,"CRM followup scheduler failed; database state retained for retry");}
            }
        }
        while(await interval.WaitForNextTickAsync(stoppingToken));
    }
}

internal sealed class CrmReminderDispatcherWorker(IServiceScopeFactory scopes,IConfiguration cfg,
    ILogger<CrmReminderDispatcherWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var interval=new PeriodicTimer(TimeSpan.FromSeconds(
            Math.Clamp(cfg.GetValue("Crm:Outbox:IntervalSeconds",10),5,120)));
        do
        {
            if(cfg.GetValue("Features:CrmBackgroundTasksEnabled",false))
            {
                try {await Dispatch(stoppingToken);}
                catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
                catch(Exception ex){logger.LogError(ex,"CRM outbox dispatch failed; pending entries retained");}
            }
        }while(await interval.WaitForNextTickAsync(stoppingToken));
    }
    private async Task Dispatch(CancellationToken ct)
    {
        await using var scope=scopes.CreateAsyncScope();
        var db=scope.ServiceProvider.GetRequiredService<CrmDbContext>();
        var publisher=scope.ServiceProvider.GetRequiredService<IRedisStreamPublisher>();
        var auth=scope.ServiceProvider.GetRequiredService<IAuthReferenceService>();
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        if(!await CrmReminderEngine.Acquire(db,5012508,ct))return;
        var now=DateTime.UtcNow;
        var jobs=await db.OutboxMessages
            .Where(x=>x.Status=="Pending" && x.NextAttemptAt<=now)
            .OrderBy(x=>x.NextAttemptAt).Take(40).ToListAsync(ct);
        foreach(var msg in jobs)
        {
            var reminder=await db.Reminders.FirstOrDefaultAsync(x=>x.Id==msg.ReminderId,ct);
            if(reminder is null){msg.Status="DeadLetter";msg.LastError="Missing CRM reminder";continue;}
            if(reminder.Status=="Cancelled"){msg.Status="Cancelled";continue;}
            // Re-check source on every retry, preventing stale reminders after manual edits.
            bool active=reminder.EntityType switch
            {
                "Followup"=>await db.Followups.AsNoTracking().AnyAsync(x=>x.Id==reminder.EntityId&&
                    x.RowVersion==reminder.TargetVersion&&(x.Status=="Pending"||x.Status=="Overdue"),ct),
                "Activity"=>await db.Activities.AsNoTracking().AnyAsync(x=>x.Id==reminder.EntityId&&
                    x.RowVersion==reminder.TargetVersion&&x.Status=="Scheduled"&&x.ArchivedAt==null,ct),
                "CreditProfile"=>await db.CreditProfiles.AsNoTracking().AnyAsync(x=>x.Id==reminder.EntityId&&
                    x.RowVersion==reminder.TargetVersion&&x.Status=="Approved"&&x.ReviewAt!=null,ct),
                "CreditContract"=>await db.CreditContracts.AsNoTracking().AnyAsync(x=>x.Id==reminder.EntityId&&
                    x.RowVersion==reminder.TargetVersion&&x.Status=="Active",ct),
                _=>false
            };
            if(!active){reminder.Status="Cancelled";msg.Status="Cancelled";continue;}
            try
            {
                var user=await auth.GetActiveAsync(reminder.RecipientUserId,ct);
                if(user is null)throw new InvalidOperationException("CRM reminder recipient is inactive in Auth");
                if(reminder.Channel=="Email" && string.IsNullOrWhiteSpace(user.Email))
                    throw new InvalidOperationException("CRM reminder has no active recipient email");
                var subject=reminder.NotificationType switch
                {
                    "crm.visit.reminder.v1"=>"Próxima visita comercial",
                    "crm.call.reminder.v1"=>"Próxima llamada comercial",
                    "crm.meeting.reminder.v1"=>"Próxima reunión comercial",
                    "crm.activity.reminder.v1"=>"Actividad comercial programada",
                    "crm.activity.overdue.v1"=>"Actividad comercial vencida",
                    "crm.credit.review.v1"=>"Revisión de crédito comercial pendiente",
                    "crm.contract.renewal.v1"=>"Renovación de contrato comercial próxima",
                    _=>"Seguimiento comercial pendiente"
                };
                var relatedActivity=reminder.EntityType=="Activity"
                    ? await db.Activities.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==reminder.EntityId,ct) : null;
                var relatedFollowup=reminder.EntityType=="Followup"
                    ? await db.Followups.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==reminder.EntityId,ct) : null;
                var dueAt=relatedActivity?.ScheduledStartAt??relatedFollowup?.DueAt;
                var title=relatedActivity?.Subject??relatedFollowup?.Subject??subject;
                var localDate=dueAt.HasValue?TimeZoneInfo.ConvertTimeFromUtc(dueAt.Value,
                    TimeZoneInfo.FindSystemTimeZoneById("America/Costa_Rica")).ToString("dd/MM/yyyy HH:mm"):null;
                var body=reminder.NotificationType=="crm.activity.overdue.v1"?
                    "Existe una actividad vencida. Revise su agenda CRM; no se registra una ejecución automática.":
                    $"Recordatorio: {title}. {(localDate is null?"":$"Fecha: {localDate} (Costa Rica). ")}Revise su agenda CRM en Dhole.";
                var payload=new {
                    eventId=reminder.Id.ToString(),
                    notificationType=reminder.NotificationType,
                    channel=reminder.Channel,
                    entityType=reminder.EntityType,
                    entityId=reminder.EntityId.ToString(),
                    subject,body,maxAttempts=5,
                    payload=new { crmReminderId=reminder.Id, source="DholeCRMService" },
                    recipients=new [] {new {userId=reminder.RecipientUserId.ToString(),
                        address=reminder.Channel=="Email"?user.Email??"":"",
                        displayName=user.DisplayName??""}}
                };
                await publisher.PublishAsync(new RedisStreamMessage {
                    StreamName=cfg["Redis:Streams:NotificationsStreamName"]??"dhole.notifications.events",
                    MessageType=msg.EventName,
                    Payload=payload,
                    Headers=new Dictionary<string,string> {
                        ["event_id"]=reminder.Id.ToString(),
                        ["source_service"]="DholeCRMService",
                        ["event_name"]=msg.EventName
                    }
                },ct);
                msg.Status="Published";msg.PublishedAt=DateTime.UtcNow;msg.LastError=null;
                reminder.Status="Queued";reminder.DispatchedAt=DateTime.UtcNow;
                reminder.RowVersion++;reminder.UpdatedAt=DateTime.UtcNow;
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(Exception ex)
            {
                msg.AttemptCount++;reminder.AttemptCount++;
                if(msg.AttemptCount>=8)
                {
                    msg.Status="DeadLetter";reminder.Status="Failed";
                }
                else
                {
                    var minutes=Math.Min(240,Math.Pow(2,msg.AttemptCount));
                    msg.NextAttemptAt=DateTime.UtcNow.AddMinutes(minutes)
                        .AddSeconds(Random.Shared.Next(0,30));
                }
                msg.LastError=ex.GetType().Name; // Never persist emails or tokens.
                logger.LogWarning("CRM outbox message {EventId} retry {Attempt}",
                    msg.Id,msg.AttemptCount);
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
