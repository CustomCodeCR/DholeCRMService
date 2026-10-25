using System.Data;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Workers.Followups;

// Finance reminders use the same durable Notifications Outbox and stable event identity as Fase 5.
// Neither credit limits nor payment terms are emitted to Redis or email.
public static class CrmFinanceReminderEngine
{
    private static async Task Enqueue(CrmDbContext db,string kind,Guid id,Guid owner,long version,
        string notificationType,int days,DateTime now,Guid creator,CancellationToken ct)
    {
        var key=$"finance:{kind}:{id:N}:{version}:{notificationType}:{days}";
        if(db.Reminders.Local.Any(x=>x.DedupeKey==key)||
            await db.Reminders.AnyAsync(x=>x.DedupeKey==key,ct))return;
        var reminder=new CrmReminder {
            EntityType=kind,EntityId=id,RecipientUserId=owner,Channel="System",
            NotificationType=notificationType,DedupeKey=key,
            TargetVersion=version,FireAt=now,CreatedByUserId=creator
        };
        db.Reminders.Add(reminder);
        db.OutboxMessages.Add(new CrmOutboxMessage {
            Id=reminder.Id,ReminderId=reminder.Id,NextAttemptAt=now
        });
    }
    public static async Task Contracts(CrmDbContext db,DateTime now,int[] thresholds,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        if(!await CrmReminderEngine.Acquire(db,5012510,ct))return;
        int max=thresholds.Length==0?60:thresholds.Max();
        var contracts=await db.CreditContracts.AsNoTracking()
            .Where(x=>x.Status=="Active" && x.RenewalDate>=now.AddDays(-2) &&
                x.RenewalDate<=now.AddDays(max+2))
            .OrderBy(x=>x.RenewalDate).Take(500).ToListAsync(ct);
        foreach(var c in contracts)
        {
            foreach(int days in thresholds.Distinct())
            {
                var threshold=c.RenewalDate.AddDays(-days);
                if(threshold>now || threshold<now.AddDays(-2))continue;
                await Enqueue(db,"CreditContract",c.Id,c.CreatedByUserId,c.RowVersion,
                    "crm.contract.renewal.v1",days,now,c.CreatedByUserId,ct);
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
    public static async Task Credits(CrmDbContext db,DateTime now,int[] thresholds,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        if(!await CrmReminderEngine.Acquire(db,5012511,ct))return;
        int max=thresholds.Length==0?30:thresholds.Max();
        var profiles=await db.CreditProfiles.AsNoTracking().Where(x=>
            x.Status=="Approved" && x.ReviewAt!=null &&
            x.ReviewAt>=now.AddDays(-2) && x.ReviewAt<=now.AddDays(max+2))
            .OrderBy(x=>x.ReviewAt).Take(500).ToListAsync(ct);
        foreach(var p in profiles)
        {
            if(p.ApprovedByUserId is not { } owner || owner==Guid.Empty)continue;
            foreach(int days in thresholds.Distinct())
            {
                var threshold=p.ReviewAt!.Value.AddDays(-days);
                if(threshold>now || threshold<now.AddDays(-2))continue;
                await Enqueue(db,"CreditProfile",p.Id,owner,p.RowVersion,
                    "crm.credit.review.v1",days,now,p.CreatedByUserId,ct);
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
    public static int[] ParseThresholds(string? value,int[] fallback)
    {
        if(string.IsNullOrWhiteSpace(value))return fallback;
        var values=value.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        if(values.Length>12||values.Length==0)return fallback;
        var parsed=new List<int>();
        foreach(var v in values)
        {
            if(!int.TryParse(v,out var number)||number<0||number>365)return fallback;
            parsed.Add(number);
        }
        return parsed.Distinct().OrderByDescending(x=>x).ToArray();
    }
}
internal sealed class CrmContractRenewalWorker(IServiceScopeFactory scopes,IConfiguration cfg,
    ILogger<CrmContractRenewalWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromHours(
            Math.Clamp(cfg.GetValue("Crm:FinanceReminders:IntervalHours",24),1,48)));
        do
        {
            if(cfg.GetValue("Features:CrmBackgroundTasksEnabled",false)&&
               cfg.GetValue("Features:CrmFinanceRemindersEnabled",false))
            {
                try
                {
                    await using var scope=scopes.CreateAsyncScope();
                    await CrmFinanceReminderEngine.Contracts(
                        scope.ServiceProvider.GetRequiredService<CrmDbContext>(),DateTime.UtcNow,
                        CrmFinanceReminderEngine.ParseThresholds(
                            cfg["Crm:FinanceReminders:ContractDays"],[60,30,15,7]),ct);
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
                catch(Exception e){logger.LogError(e,"CRM contract renewal reminder scheduling failed");}
            }
        }while(await timer.WaitForNextTickAsync(ct));
    }
}
internal sealed class CrmCreditReviewWorker(IServiceScopeFactory scopes,IConfiguration cfg,
    ILogger<CrmCreditReviewWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromHours(
            Math.Clamp(cfg.GetValue("Crm:FinanceReminders:IntervalHours",24),1,48)));
        do
        {
            if(cfg.GetValue("Features:CrmBackgroundTasksEnabled",false)&&
               cfg.GetValue("Features:CrmFinanceRemindersEnabled",false))
            {
                try
                {
                    await using var scope=scopes.CreateAsyncScope();
                    await CrmFinanceReminderEngine.Credits(
                        scope.ServiceProvider.GetRequiredService<CrmDbContext>(),DateTime.UtcNow,
                        CrmFinanceReminderEngine.ParseThresholds(
                            cfg["Crm:FinanceReminders:CreditDays"],[30,7,0]),ct);
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
                catch(Exception e){logger.LogError(e,"CRM credit review reminder scheduling failed");}
            }
        }while(await timer.WaitForNextTickAsync(ct));
    }
}
