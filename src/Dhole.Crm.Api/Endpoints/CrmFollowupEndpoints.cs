using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;
public sealed record FollowupPolicyRequest(long? ExpectedVersion,bool Enabled,Guid OwnerUserId,string ChannelStrategy,
    int FrequencyDays,DateTime NextDueAt,string TimezoneId,int ReminderLeadMinutes,DateTime? PausedUntil);
public sealed record FollowupCreateRequest(Guid? AccountId,Guid? LeadId,Guid? OpportunityId,
    Guid AssignedUserId,string Channel,DateTime DueAt);
public sealed record FollowupRescheduleRequest(long ExpectedVersion,DateTime DueAt,string Reason);
public sealed record FollowupCompleteRequest(long ExpectedVersion,string Outcome,DateTime? NextFollowupAt);
public sealed record FollowupCancelRequest(long ExpectedVersion,string Reason);

internal static class CrmFollowupEndpoints
{
    public static RouteGroupBuilder MapFollowups(this RouteGroupBuilder group)
    {
        group.MapGet("/accounts/{accountId:guid}/followup-policy",GetPolicy);
        group.MapPut("/accounts/{accountId:guid}/followup-policy",UpsertPolicy);
        group.MapGet("/followups",List);
        group.MapGet("/followups/{id:guid}",Get);
        group.MapPost("/followups",Create);
        group.MapPost("/followups/{id:guid}/reschedule",Reschedule);
        group.MapPost("/followups/{id:guid}/complete",Complete);
        group.MapPost("/followups/{id:guid}/cancel",Cancel);
        group.MapGet("/followups/{id:guid}/history",History);
        group.MapGet("/reminders",ListReminders);
        group.MapGet("/messaging/outbox",ListOutbox);
        group.MapPost("/reminders/{id:guid}/replay",ReplayUndelivered);
        return group;
    }
    private static bool Manage(HttpContext h)=>CrmAccess.Scope(h,"crm.followup.manage");
    private static bool Tasks(HttpContext h)=>CrmAccess.Scope(h,"crm.task.manage");
    private static bool Utc(DateTime dt)=>dt.Kind==DateTimeKind.Utc;
    private static async Task<bool> AccountVisible(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)=>
        await db.Accounts.AsNoTracking().AnyAsync(a=>a.Id==id && a.ArchivedAt==null &&
            (a.ExecutiveUserId==CrmAccess.UserId(h)||CrmAccess.Scope(h,"crm.account.read-all")),ct);
    private static bool HasAccess(Followup x,HttpContext h) =>
        x.AssignedUserId==CrmAccess.UserId(h) ||
        (x.AccountId.HasValue && CrmAccess.Scope(h,"crm.account.read-all")) ||
        (x.LeadId.HasValue && CrmAccess.Scope(h,"crm.lead.read-all")) ||
        (x.OpportunityId.HasValue && CrmAccess.Scope(h,"crm.opportunity.read-all"));
    private static async Task<IResult> GetPolicy(Guid accountId,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!Manage(h)||!await AccountVisible(accountId,db,h,ct))return CrmAccess.Denied();
        var p=await db.AccountFollowupPolicies.AsNoTracking().FirstOrDefaultAsync(x=>x.AccountId==accountId,ct);
        return p is null?Results.NotFound():Results.Ok(p);
    }
    private static async Task<IResult> UpsertPolicy(Guid accountId,FollowupPolicyRequest r,
        CrmDbContext db,HttpContext h,IAuthReferenceService auth,CancellationToken ct)
    {
        if(!Manage(h)||!await AccountVisible(accountId,db,h,ct))return CrmAccess.Denied();
        if(r.FrequencyDays is < 1 or > 365||r.ReminderLeadMinutes is < 0 or > 10080||
            !Utc(r.NextDueAt)||(r.PausedUntil.HasValue&&!Utc(r.PausedUntil.Value))||
            r.ChannelStrategy is not ("BOTH" or "SYSTEM" or "EMAIL")||
            string.IsNullOrWhiteSpace(r.TimezoneId)||r.TimezoneId.Length>128)
            return CrmDataEndpoints.Bad("Invalid followup schedule or channel policy");
        try { _=TimeZoneInfo.FindSystemTimeZoneById(r.TimezoneId); }
        catch(TimeZoneNotFoundException){return CrmDataEndpoints.Bad("Unknown timezone");}
        if(r.OwnerUserId==Guid.Empty||await auth.GetActiveAsync(r.OwnerUserId,ct) is null)
            return CrmDataEndpoints.Bad("Owner must be active in Auth");
        var now=DateTime.UtcNow;
        var p=await db.AccountFollowupPolicies.FirstOrDefaultAsync(x=>x.AccountId==accountId,ct);
        if(p is null)
        {
            if(r.ExpectedVersion.HasValue)return CrmDataEndpoints.Conflict("Policy is not yet present");
            p=new AccountFollowupPolicy {AccountId=accountId,CreatedByUserId=CrmAccess.UserId(h)};
            db.AccountFollowupPolicies.Add(p);
        }
        else
        {
            if(r.ExpectedVersion!=p.RowVersion)return CrmDataEndpoints.Conflict("Policy updated concurrently");
            p.RowVersion++;p.UpdatedByUserId=CrmAccess.UserId(h);p.UpdatedAt=now;
        }
        p.Enabled=r.Enabled;p.OwnerUserId=r.OwnerUserId;p.ChannelStrategy=r.ChannelStrategy;
        p.FrequencyDays=r.FrequencyDays;p.NextDueAt=r.NextDueAt;p.TimezoneId=r.TimezoneId;
        p.ReminderLeadMinutes=r.ReminderLeadMinutes;p.PausedUntil=r.PausedUntil;
        var result=await CrmDataEndpoints.Commit(db,ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(p):result;
    }
    private static async Task<IResult> List(CrmDbContext db,HttpContext h,Guid? accountId,string? status,
        int? page,int? pageSize,CancellationToken ct)
    {
        if(!Manage(h)&&!Tasks(h))return CrmAccess.Denied();
        var query=db.Followups.AsNoTracking().AsQueryable();
        var actor=CrmAccess.UserId(h);
        var accounts=CrmAccess.Scope(h,"crm.account.read-all");
        var leads=CrmAccess.Scope(h,"crm.lead.read-all");
        var opportunities=CrmAccess.Scope(h,"crm.opportunity.read-all");
        query=query.Where(x=>x.AssignedUserId==actor ||
            (accounts && x.AccountId!=null) ||
            (leads && x.LeadId!=null) ||
            (opportunities && x.OpportunityId!=null));
        if(accountId.HasValue)query=query.Where(x=>x.AccountId==accountId);
        if(!string.IsNullOrWhiteSpace(status))query=query.Where(x=>x.Status==status);
        var n=Math.Max(1,page??1);var s=Math.Clamp(pageSize??25,1,100);var total=await query.CountAsync(ct);
        return Results.Ok(new{total,page=n,pageSize=s,items=await query.OrderBy(x=>x.DueAt)
            .ThenBy(x=>x.Id).Skip((n-1)*s).Take(s).ToListAsync(ct)});
    }
    private static async Task<IResult> Get(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!Manage(h)&&!Tasks(h))return CrmAccess.Denied();
        var x=await db.Followups.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id,ct);
        return x is null?Results.NotFound():HasAccess(x,h)?Results.Ok(x):CrmAccess.Denied();
    }
    private static async Task<IResult> Create(FollowupCreateRequest r,CrmDbContext db,HttpContext h,
        IAuthReferenceService auth,CancellationToken ct)
    {
        if(!Manage(h)&&!Tasks(h))return CrmAccess.Denied();
        if(!Utc(r.DueAt)||r.DueAt<DateTime.UtcNow.AddDays(-1)||
            r.Channel is not ("EMAIL" or "SYSTEM" or "PHONE" or "VISIT")||
            r.AssignedUserId==Guid.Empty)return CrmDataEndpoints.Bad("Invalid followup schedule or channel");
        if(!await CommercialAccess.SubjectCanRead(db,h,r.AccountId,r.LeadId,r.OpportunityId,ct))
            return CrmAccess.Denied();
        if(r.AssignedUserId!=CrmAccess.UserId(h)&&
           !CrmAccess.Scope(h,"crm.account.read-all")&&!CrmAccess.Scope(h,"crm.opportunity.assign"))
            return CrmAccess.Denied();
        if(await auth.GetActiveAsync(r.AssignedUserId,ct) is null)
            return CrmDataEndpoints.Bad("Assignee is not active");
        var x=new Followup {AccountId=r.AccountId,LeadId=r.LeadId,OpportunityId=r.OpportunityId,
            AssignedUserId=r.AssignedUserId,Channel=r.Channel,DueAt=r.DueAt,
            CreatedByUserId=CrmAccess.UserId(h)};
        db.Followups.Add(x);
        db.FollowupHistories.Add(new FollowupHistory {FollowupId=x.Id,ActorUserId=CrmAccess.UserId(h),
            Action="Created",NewDueAt=r.DueAt,CreatedByUserId=CrmAccess.UserId(h)});
        var ok=await CrmDataEndpoints.Commit(db,ct);
        return ok is Microsoft.AspNetCore.Http.HttpResults.Ok?
            Results.Created("/api/crm/followups/"+x.Id,x):ok;
    }
    private static async Task<IResult> Reschedule(Guid id,FollowupRescheduleRequest r,CrmDbContext db,
        HttpContext h,CancellationToken ct)
    {
        if(!Manage(h)&&!Tasks(h))return CrmAccess.Denied();
        var x=await db.Followups.FindAsync([id],ct);if(x is null)return Results.NotFound();
        if(!HasAccess(x,h))return CrmAccess.Denied();
        if(x.RowVersion!=r.ExpectedVersion)return CrmDataEndpoints.Conflict("Concurrent followup update");
        if(x.Status is not ("Pending" or "Overdue")||!Utc(r.DueAt)||
            r.DueAt<DateTime.UtcNow||string.IsNullOrWhiteSpace(r.Reason)||r.Reason.Length>2000)
            return CrmDataEndpoints.Bad("Invalid reschedule");
        db.FollowupHistories.Add(new FollowupHistory {FollowupId=id,ActorUserId=CrmAccess.UserId(h),
            Action="Rescheduled",Reason=r.Reason,PreviousDueAt=x.DueAt,NewDueAt=r.DueAt,
            CreatedByUserId=CrmAccess.UserId(h)});
        x.DueAt=r.DueAt;x.Status="Pending";x.RowVersion++;
        x.UpdatedByUserId=CrmAccess.UserId(h);x.UpdatedAt=DateTime.UtcNow;
        var ok=await CrmDataEndpoints.Commit(db,ct);
        return ok is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(x):ok;
    }
    private static async Task<IResult> Complete(Guid id,FollowupCompleteRequest r,
        CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!Manage(h)&&!Tasks(h))return CrmAccess.Denied();
        var x=await db.Followups.FindAsync([id],ct);if(x is null)return Results.NotFound();
        if(!HasAccess(x,h))return CrmAccess.Denied();
        if(x.RowVersion!=r.ExpectedVersion)return CrmDataEndpoints.Conflict("Concurrent followup update");
        if(x.Status is not ("Pending" or "Overdue")||string.IsNullOrWhiteSpace(r.Outcome)||
            r.Outcome.Length>2000||(r.NextFollowupAt is not null&&!Utc(r.NextFollowupAt.Value)))
            return CrmDataEndpoints.Bad("Invalid completion");
        var now=DateTime.UtcNow;
        x.Status="Completed";x.CompletedAt=now;x.Outcome=r.Outcome.Trim();
        x.NextFollowupAt=r.NextFollowupAt;x.RowVersion++;x.UpdatedByUserId=CrmAccess.UserId(h);x.UpdatedAt=now;
        db.FollowupHistories.Add(new FollowupHistory {FollowupId=id,ActorUserId=CrmAccess.UserId(h),
            Action="Completed",Reason=r.Outcome,PreviousDueAt=x.DueAt,
            NewDueAt=r.NextFollowupAt,CreatedByUserId=CrmAccess.UserId(h)});
        // Manual next followup overrides the automatic policy only if it has not changed.
        if(r.NextFollowupAt is { } requested && x.PolicyId is { } policyId)
        {
            var policy=await db.AccountFollowupPolicies.FindAsync([policyId],ct);
            if(policy is not null && policy.Enabled &&
                (policy.NextDueAt<=x.DueAt||policy.NextDueAt==x.PolicyOccurrenceAt))
            {
                policy.NextDueAt=requested;policy.RowVersion++;
                policy.UpdatedByUserId=CrmAccess.UserId(h);policy.UpdatedAt=now;
            }
        }
        var ok=await CrmDataEndpoints.Commit(db,ct);
        return ok is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(x):ok;
    }
    private static async Task<IResult> Cancel(Guid id,FollowupCancelRequest r,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!Manage(h)&&!Tasks(h))return CrmAccess.Denied();
        var x=await db.Followups.FindAsync([id],ct);if(x is null)return Results.NotFound();
        if(!HasAccess(x,h))return CrmAccess.Denied();
        if(x.RowVersion!=r.ExpectedVersion)return CrmDataEndpoints.Conflict("Concurrent followup update");
        if(x.Status is not ("Pending" or "Overdue")||
            string.IsNullOrWhiteSpace(r.Reason)||r.Reason.Length>2000)
            return CrmDataEndpoints.Bad("Cancellation reason required");
        x.Status="Cancelled";x.RowVersion++;x.UpdatedByUserId=CrmAccess.UserId(h);x.UpdatedAt=DateTime.UtcNow;
        db.FollowupHistories.Add(new FollowupHistory {FollowupId=id,ActorUserId=CrmAccess.UserId(h),
            Action="Cancelled",Reason=r.Reason,CreatedByUserId=CrmAccess.UserId(h)});
        var ok=await CrmDataEndpoints.Commit(db,ct);
        return ok is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(x):ok;
    }
    private static async Task<IResult> History(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!Manage(h)&&!Tasks(h))return CrmAccess.Denied();
        var x=await db.Followups.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id,ct);
        if(x is null)return Results.NotFound();
        if(!HasAccess(x,h))return CrmAccess.Denied();
        return Results.Ok(await db.FollowupHistories.AsNoTracking().Where(x=>x.FollowupId==id)
            .OrderBy(x=>x.CreatedAt).ToListAsync(ct));
    }
    private static async Task<IResult> ListOutbox(CrmDbContext db,HttpContext h,
        string? status,int? page,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.reminder.replay"))return CrmAccess.Denied();
        var q=db.OutboxMessages.AsNoTracking().AsQueryable();
        if(!string.IsNullOrWhiteSpace(status))q=q.Where(x=>x.Status==status);
        return Results.Ok(await q.OrderByDescending(x=>x.CreatedAt)
            .Skip((Math.Max(1,page??1)-1)*50).Take(50)
            .Select(x=>new {x.Id,x.ReminderId,x.EventName,x.Status,
                x.AttemptCount,x.NextAttemptAt,x.PublishedAt}).ToListAsync(ct));
    }
    private static async Task<IResult> ReplayUndelivered(Guid id,CrmDbContext db,
        HttpContext h,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.reminder.replay"))return CrmAccess.Denied();
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var rem=await db.Reminders.FindAsync([id],ct);
        var item=await db.OutboxMessages.FirstOrDefaultAsync(x=>x.ReminderId==id,ct);
        if(rem is null||item is null)return Results.NotFound();
        if(item.Status!="DeadLetter"||rem.Status!="Failed"||item.PublishedAt is not null)
            return CrmDataEndpoints.Conflict("Only unpublished dead-letter CRM reminders can be replayed");
        item.Status="Pending";item.AttemptCount=0;item.NextAttemptAt=DateTime.UtcNow;
        item.LastError=null;item.LeaseUntil=null;
        rem.Status="Pending";rem.RowVersion++;rem.UpdatedAt=DateTime.UtcNow;
        rem.UpdatedByUserId=CrmAccess.UserId(h);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Accepted("/api/crm/reminders?status=Pending",
            new {reminderId=id,status="Pending"});
    }
    private static async Task<IResult> ListReminders(CrmDbContext db,HttpContext h,string? status,int? page,CancellationToken ct)
    {
        if(!Manage(h)&&!Tasks(h))return CrmAccess.Denied();
        var query=db.Reminders.AsNoTracking().AsQueryable();
        if(!CrmAccess.Scope(h,"crm.account.read-all")&&!CrmAccess.Scope(h,"crm.opportunity.read-all"))
            query=query.Where(x=>x.RecipientUserId==CrmAccess.UserId(h));
        if(!string.IsNullOrWhiteSpace(status))query=query.Where(x=>x.Status==status);
        return Results.Ok(await query.OrderByDescending(x=>x.FireAt)
            .Skip((Math.Max(1,page??1)-1)*50).Take(50).ToListAsync(ct));
    }
}
