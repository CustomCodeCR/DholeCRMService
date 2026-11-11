using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;
public sealed record FollowupPolicyRequest(long? ExpectedVersion,bool Enabled,Guid OwnerUserId,string ChannelStrategy,
    int FrequencyDays,DateTime NextDueAt,string TimezoneId,int ReminderLeadMinutes,DateTime? PausedUntil);
public sealed record FollowupCreateRequest(Guid? AccountId,Guid? LeadId,Guid? OpportunityId,
    Guid AssignedUserId,string Channel,DateTime DueAt,
    string? Subject = null,int? ReminderLeadMinutes = null,string? ReminderChannelStrategy = null);
public sealed record FollowupRescheduleRequest(long ExpectedVersion,DateTime DueAt,string Reason);
public sealed record FollowupCompleteRequest(long ExpectedVersion,string Outcome,DateTime? NextFollowupAt);
public sealed record FollowupCancelRequest(long ExpectedVersion,string Reason);
public sealed record FollowupReopenRequest(long ExpectedVersion,string Reason);

internal static class CrmFollowupEndpoints
{
    public static RouteGroupBuilder MapFollowups(this RouteGroupBuilder group)
    {
        group.MapGet("/accounts/{accountId:guid}/followup-policy",GetPolicy);
        group.MapPut("/accounts/{accountId:guid}/followup-policy",UpsertPolicy);
        group.MapGet("/followups",List);
        group.MapGet("/followups/{id:guid}",Get);
        group.MapGet("/followups/{id:guid}/origin",GetOrigin);
        group.MapPost("/followups",Create);
        group.MapPost("/followups/{id:guid}/reschedule",Reschedule);
        group.MapPost("/followups/{id:guid}/complete",Complete);
        group.MapPost("/followups/{id:guid}/reopen",Reopen);
        group.MapPost("/followups/{id:guid}/cancel",Cancel);
        group.MapPost("/followups/{id:guid}/archive",Archive);

        group.MapGet("/followups/{id:guid}/history",History);
        group.MapGet("/followups/{id:guid}/parent-activity",ParentActivity);
        group.MapGet("/reminders",ListReminders);
        group.MapGet("/messaging/outbox",ListOutbox);
        group.MapPost("/reminders/{id:guid}/replay",ReplayUndelivered);
        return group;
    }
    private static bool Manage(HttpContext h)=>CrmAccess.Scope(h,"crm.followup.manage");
    private static bool Tasks(HttpContext h)=>CrmAccess.Scope(h,"crm.task.manage");
    private static bool CanReadCalendar(HttpContext h)=>Manage(h)||Tasks(h)||
        CrmDataEndpoints.HasRead(h,"activity")||
        CrmAccess.CanManageAgendaAcrossUsers(h)||
        CrmAccess.Scope(h,"crm.account.read-all") ||
        CrmAccess.IsCalendarAdministrator(h);
    private static bool Utc(DateTime dt)=>dt.Kind==DateTimeKind.Utc;
    private static async Task<bool> AccountVisible(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)=>
        await CrmAccess.CanSeeAccount(db,h,id,ct);
    private static async Task<bool> HasAccess(Followup x,CrmDbContext db,HttpContext h,CancellationToken ct) =>
        (!x.AccountId.HasValue || await CrmAccess.CanSeeAccount(db,h,x.AccountId.Value,ct)) &&
        (CrmAccess.CanManageAgendaAcrossUsers(h) ||
         x.AssignedUserId==CrmAccess.UserId(h) ||
         (x.AccountId.HasValue && CrmAccess.Scope(h,"crm.account.read-all")) ||
         (x.LeadId.HasValue && CrmAccess.Scope(h,"crm.lead.read-all")) ||
         (x.OpportunityId.HasValue && CrmAccess.Scope(h,"crm.opportunity.read-all")));
    // Calendar administrators may view other users' followups but cannot
    // reschedule, cancel or complete them without an existing write grant.
    private static Task<bool> HasCalendarAccess(Followup x,CrmDbContext db,HttpContext h,CancellationToken ct) =>
        CrmAccess.IsCalendarAdministrator(h) ? Task.FromResult(true) : HasAccess(x,db,h,ct);

    private static async Task<IResult> GetPolicy(Guid accountId,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!(Manage(h)||CrmAccess.IsCalendarAdministrator(h)) ||
            !await CrmAccess.CanSeeCalendarAccount(db,h,accountId,ct))return CrmAccess.Denied();
        var p=await db.AccountFollowupPolicies.AsNoTracking().FirstOrDefaultAsync(x=>x.AccountId==accountId,ct);
        // Absence of a policy is the normal create state, not a broken account.
        return p is null?Results.Json((object?)null):Results.Ok(p);
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
    private static async Task<IResult> List(CrmDbContext db,HttpContext h,Guid? accountId,Guid? leadId,string? status,
        DateTime? from,DateTime? to,Guid? ownerId,int? page,int? pageSize,CancellationToken ct)
    {
        if(!CanReadCalendar(h))return CrmAccess.Denied();
        var visibleAccountIds = CrmAccess.CalendarVisibleAccounts(db,h).Select(a=>a.Id);
        var query=db.Followups.AsNoTracking().Where(x => x.ArchivedAt == null &&
            (!x.AccountId.HasValue || visibleAccountIds.Contains(x.AccountId.Value)));
        var actor=CrmAccess.UserId(h);
        var accounts=CrmAccess.Scope(h,"crm.account.read-all");
        var leads=CrmAccess.Scope(h,"crm.lead.read-all");
        var opportunities=CrmAccess.Scope(h,"crm.opportunity.read-all");
        if(!CrmAccess.IsCalendarAdministrator(h) && !CrmAccess.CanManageAgendaAcrossUsers(h))
            query=query.Where(x=>x.AssignedUserId==actor ||
                (accounts && x.AccountId!=null) ||
                (leads && x.LeadId!=null) ||
                (opportunities && x.OpportunityId!=null));
        if(ownerId.HasValue)
        {
            if(ownerId.Value!=actor && !CrmAccess.IsCalendarAdministrator(h) &&
                !CrmAccess.CanManageAgendaAcrossUsers(h) &&
                !accounts && !leads && !opportunities)return CrmAccess.Denied();
            query=query.Where(x=>x.AssignedUserId==ownerId.Value);
        }
        if(accountId.HasValue)query=query.Where(x=>x.AccountId==accountId);
        // Prospect calendars only request tasks linked to that lead.
        if(leadId.HasValue)query=query.Where(x=>x.LeadId==leadId);
        if(!string.IsNullOrWhiteSpace(status))query=query.Where(x=>x.Status==status);
        if(from.HasValue)query=query.Where(x=>x.DueAt>=from.Value);
        if(to.HasValue)query=query.Where(x=>x.DueAt<to.Value);
        var n=Math.Max(1,page??1);var s=Math.Clamp(pageSize??25,1,100);var total=await query.CountAsync(ct);
        return Results.Ok(new{total,page=n,pageSize=s,items=await query.OrderBy(x=>x.DueAt)
            .ThenBy(x=>x.Id).Skip((n-1)*s).Take(s).ToListAsync(ct)});
    }
    private static async Task<IResult> Get(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CanReadCalendar(h))return CrmAccess.Denied();
        var x=await db.Followups.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id && x.ArchivedAt==null,ct);
        return x is null?Results.NotFound():await HasCalendarAccess(x,db,h,ct)?Results.Ok(x):CrmAccess.Denied();
    }

    // Resolve the original activity from the append-only creation history.
    // Only return its title if the caller is also allowed to read the activity.
    private static async Task<IResult> ParentActivity(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CanReadCalendar(h))return CrmAccess.Denied();
        var followup=await db.Followups.AsNoTracking()
            .FirstOrDefaultAsync(x=>x.Id==id && x.ArchivedAt==null,ct);
        if(followup is null)return Results.NotFound();
        if(!await HasCalendarAccess(followup,db,h,ct))return CrmAccess.Denied();
        // Policy-generated followups have no Activity. Return their authorized
        // account policy context instead of presenting them as unexplained UUIDs.
        // These values describe the current policy, not a historical snapshot.
        if(followup.PolicyId is { } policyId)
        {
            var policy=await db.AccountFollowupPolicies.AsNoTracking()
                .Where(p=>p.Id==policyId && p.AccountId==followup.AccountId)
                .Select(p=>new {
                    p.FrequencyDays,p.TimezoneId,p.ChannelStrategy,
                    p.ReminderLeadMinutes,p.Enabled,p.PausedUntil,p.NextDueAt
                }).FirstOrDefaultAsync(ct);
            return Results.Ok(new {
                activityId=(Guid?)null,sourceType="POLICY",
                policyOccurrenceAt=followup.PolicyOccurrenceAt,policy
            });
        }
        if(!CrmDataEndpoints.HasRead(h,"activity") && !CrmAccess.IsCalendarAdministrator(h))
            return Results.Ok(new{activityId=(Guid?)null,sourceType="UNAVAILABLE"});
        var origins=await db.FollowupHistories.AsNoTracking()
            .Where(x=>x.FollowupId==id && x.Action=="Created")
            .Select(x=>x.Reason).ToListAsync(ct);
        Guid? parentId=null;
        foreach(var origin in origins)
        {
            if(string.IsNullOrWhiteSpace(origin))continue;
            const string fromActivity="Acuerdo registrado en actividad comercial ";
            const string fromNote="Acuerdo de observación ";
            if(origin.StartsWith(fromActivity,StringComparison.Ordinal) &&
                Guid.TryParse(origin[fromActivity.Length..],out var directId))
            {
                parentId=directId;break;
            }
            if(origin.StartsWith(fromNote,StringComparison.Ordinal) &&
                Guid.TryParse(origin[fromNote.Length..],out var noteId))
            {
                parentId=await db.ActivityNotes.AsNoTracking()
                    .Where(x=>x.Id==noteId).Select(x=>(Guid?)x.ActivityId)
                    .FirstOrDefaultAsync(ct);
                if(parentId.HasValue)break;
            }
        }
        if(!parentId.HasValue)return Results.Ok(new{activityId=(Guid?)null,sourceType="MANUAL"});
        var activity=await db.Activities.AsNoTracking()
            .FirstOrDefaultAsync(x=>x.Id==parentId.Value && x.ArchivedAt==null,ct);
        if(activity is null ||
            (!CommercialAccess.ActivityOwns(h,activity) && !CrmAccess.IsCalendarAdministrator(h)) ||
            (activity.AccountId.HasValue &&
             !await CrmAccess.CanSeeCalendarAccount(db,h,activity.AccountId.Value,ct)))
            return Results.Ok(new{activityId=(Guid?)null});
        return Results.Ok(new{activityId=(Guid?)activity.Id,activitySubject=activity.Subject,sourceType="ACTIVITY"});
    }

    private static async Task<IResult> GetOrigin(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CanReadCalendar(h))return CrmAccess.Denied();
        var followup=await db.Followups.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id&&x.ArchivedAt==null,ct);
        if(followup is null)return Results.NotFound();
        if(!await HasAccess(followup,db,h,ct))return CrmAccess.Denied();
        var origin=followup.OriginActivityId;
        if(origin is null)
        {
            // Legacy follow-ups encoded the relationship in creation history.
            var reason=await db.FollowupHistories.AsNoTracking()
                .Where(x=>x.FollowupId==id&&x.Action=="Created")
                .Select(x=>x.Reason).FirstOrDefaultAsync(ct);
            const string byActivity="Acuerdo registrado en actividad comercial ";
            const string byNote="Acuerdo de observación ";
            if(reason?.StartsWith(byActivity,StringComparison.Ordinal)==true &&
                Guid.TryParse(reason[byActivity.Length..],out var activityId))
                origin=activityId;
            else if(reason?.StartsWith(byNote,StringComparison.Ordinal)==true &&
                Guid.TryParse(reason[byNote.Length..],out var noteId))
                origin=await db.ActivityNotes.AsNoTracking()
                    .Where(x=>x.Id==noteId).Select(x=>(Guid?)x.ActivityId).FirstOrDefaultAsync(ct);
        }
        if(origin is null)return Results.Json((object?)null);
        var activity=await db.Activities.AsNoTracking()
            .Where(x=>x.Id==origin&&x.ArchivedAt==null)
            .Select(x=>new{x.Id,x.Subject,x.Type,x.Status,x.ScheduledStartAt,x.AccountId,x.LeadId})
            .FirstOrDefaultAsync(ct);
        return activity is null?Results.Json((object?)null):Results.Ok(activity);
    }

    private static async Task<IResult> Create(FollowupCreateRequest r,CrmDbContext db,HttpContext h,
        IAuthReferenceService auth,CancellationToken ct)
    {
        if(!Manage(h)&&!Tasks(h)&&
            !(CrmAccess.Scope(h,"crm.activity.create") && CrmAccess.CanManageAgendaAcrossUsers(h)))
            return CrmAccess.Denied();
        if(!Utc(r.DueAt)||r.DueAt<DateTime.UtcNow.AddDays(-1)||
            r.Subject?.Length>250||r.ReminderLeadMinutes is < 0 or > 10080||
            (r.ReminderChannelStrategy is not null && r.ReminderChannelStrategy is not ("BOTH" or "EMAIL" or "SYSTEM"))||
            r.Channel is not ("EMAIL" or "SYSTEM" or "PHONE" or "VISIT")||
            r.AssignedUserId==Guid.Empty)return CrmDataEndpoints.Bad("Invalid followup schedule or channel");
        if(!await CommercialAccess.SubjectCanRead(db,h,r.AccountId,r.LeadId,r.OpportunityId,ct))
            return CrmAccess.Denied();
        if(r.AssignedUserId!=CrmAccess.UserId(h)&&
           !CrmAccess.CanManageAgendaAcrossUsers(h)&&
           !CrmAccess.Scope(h,"crm.account.read-all")&&!CrmAccess.Scope(h,"crm.opportunity.assign"))
            return CrmAccess.Denied();
        var activeAssignee=await auth.GetActiveAsync(r.AssignedUserId,ct);
        if(activeAssignee is null)return CrmDataEndpoints.Bad("Assignee is not active");
        if(r.AssignedUserId!=CrmAccess.UserId(h) && !CrmAccess.IsSeller(activeAssignee))
            return CrmDataEndpoints.Bad("Assignee must have the Vendedor role in Auth");
        var x=new Followup {AccountId=r.AccountId,LeadId=r.LeadId,OpportunityId=r.OpportunityId,
            AssignedUserId=r.AssignedUserId,Channel=r.Channel,DueAt=r.DueAt,
            Subject=r.Subject?.Trim(),ReminderLeadMinutes=r.ReminderLeadMinutes,
            ReminderChannelStrategy=r.ReminderChannelStrategy,
            CreatedByUserId=CrmAccess.UserId(h)};
        db.Followups.Add(x);
        db.FollowupHistories.Add(new FollowupHistory {FollowupId=x.Id,ActorUserId=CrmAccess.UserId(h),
            Action="Created",NewDueAt=r.DueAt,CreatedByUserId=CrmAccess.UserId(h)});
        await CrmReminderEnqueue.QueueAsync(db,"Followup",x.Id,x.AccountId,
            x.AssignedUserId,x.RowVersion,x.DueAt,x.ReminderLeadMinutes,
            x.ReminderChannelStrategy,"crm.followup.reminder.v1",x.CreatedByUserId,ct);
        var ok=await CrmDataEndpoints.Commit(db,ct);
        return ok is Microsoft.AspNetCore.Http.HttpResults.Ok?
            Results.Created("/api/crm/followups/"+x.Id,x):ok;
    }
    private static async Task<IResult> Reopen(Guid id,FollowupReopenRequest r,
        CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!Manage(h)&&!Tasks(h))return CrmAccess.Denied();
        var x=await db.Followups.FindAsync([id],ct);
        if(x is null||x.ArchivedAt!=null)return Results.NotFound();
        if(!await HasAccess(x,db,h,ct))return CrmAccess.Denied();
        if(x.RowVersion!=r.ExpectedVersion)return CrmDataEndpoints.Conflict("Concurrent followup update");
        if(x.Status!="Completed"||string.IsNullOrWhiteSpace(r.Reason)||r.Reason.Length>500)
            return CrmDataEndpoints.Bad("Only completed commitments can be reopened with a reason");
        x.Status=x.DueAt<DateTime.UtcNow?"Overdue":"Pending";
        x.CompletedAt=null;x.Outcome=null;x.NextFollowupAt=null;
        x.RowVersion++;x.UpdatedAt=DateTime.UtcNow;x.UpdatedByUserId=CrmAccess.UserId(h);
        db.FollowupHistories.Add(new FollowupHistory {
            FollowupId=id,ActorUserId=CrmAccess.UserId(h),Action="Reopened",
            Reason=r.Reason.Trim(),CreatedByUserId=CrmAccess.UserId(h)
        });
        if(x.DueAt>DateTime.UtcNow)
            await CrmReminderEnqueue.QueueAsync(db,"Followup",x.Id,x.AccountId,
                x.AssignedUserId,x.RowVersion,x.DueAt,x.ReminderLeadMinutes,
                x.ReminderChannelStrategy,"crm.followup.reminder.v1",x.CreatedByUserId,ct);
        var result=await CrmDataEndpoints.Commit(db,ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(x):result;
    }

    private static async Task<IResult> Reschedule(Guid id,FollowupRescheduleRequest r,CrmDbContext db,
        HttpContext h,CancellationToken ct)
    {
        if(!Manage(h)&&!Tasks(h))return CrmAccess.Denied();
        var x=await db.Followups.FindAsync([id],ct);if(x is null)return Results.NotFound();
        if(!await HasAccess(x,db,h,ct))return CrmAccess.Denied();
        if(x.RowVersion!=r.ExpectedVersion)return CrmDataEndpoints.Conflict("Concurrent followup update");
        if(x.Status is not ("Pending" or "Overdue")||!Utc(r.DueAt)||
            r.DueAt<DateTime.UtcNow||string.IsNullOrWhiteSpace(r.Reason)||r.Reason.Length>2000)
            return CrmDataEndpoints.Bad("Invalid reschedule");
        db.FollowupHistories.Add(new FollowupHistory {FollowupId=id,ActorUserId=CrmAccess.UserId(h),
            Action="Rescheduled",Reason=r.Reason,PreviousDueAt=x.DueAt,NewDueAt=r.DueAt,
            CreatedByUserId=CrmAccess.UserId(h)});
        x.DueAt=r.DueAt;x.Status="Pending";x.RowVersion++;
        x.UpdatedByUserId=CrmAccess.UserId(h);x.UpdatedAt=DateTime.UtcNow;
        await CrmReminderEnqueue.QueueAsync(db,"Followup",x.Id,x.AccountId,
            x.AssignedUserId,x.RowVersion,x.DueAt,x.ReminderLeadMinutes,
            x.ReminderChannelStrategy,"crm.followup.reminder.v1",x.CreatedByUserId,ct);
        var ok=await CrmDataEndpoints.Commit(db,ct);
        return ok is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(x):ok;
    }
    // Completing a commitment from its parent activity is an activity-write action.
    // This covers salespeople without crm.task.manage, but never grants blanket followup mutation.
    // Older CRM commitments can identify their origin through their immutable Created history.
    private static async Task<bool> CanCompleteActivityCommitment(Followup followup,CrmDbContext db,
        HttpContext h,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.activity.update"))return false;
        Guid? origin=followup.OriginActivityId;
        if(!origin.HasValue)
        {
            var reasons=await db.FollowupHistories.AsNoTracking()
                .Where(history=>history.FollowupId==followup.Id && history.Action=="Created")
                .Select(history=>history.Reason).ToListAsync(ct);
            const string activityPrefix="Acuerdo registrado en actividad comercial ";
            const string notePrefix="Acuerdo de observación ";
            foreach(var reason in reasons)
            {
                if(reason is null)continue;
                if(reason.StartsWith(activityPrefix,StringComparison.Ordinal) &&
                   Guid.TryParse(reason[activityPrefix.Length..],out var parentId))
                {origin=parentId;break;}
                if(reason.StartsWith(notePrefix,StringComparison.Ordinal) &&
                   Guid.TryParse(reason[notePrefix.Length..],out var noteId))
                {
                    origin=await db.ActivityNotes.AsNoTracking().Where(n=>n.Id==noteId)
                        .Select(n=>(Guid?)n.ActivityId).FirstOrDefaultAsync(ct);
                    if(origin.HasValue)break;
                }
            }
        }
        if(!origin.HasValue)return false;
        var activity=await db.Activities.AsNoTracking()
            .FirstOrDefaultAsync(a=>a.Id==origin && a.ArchivedAt==null,ct);
        return activity is not null && CommercialAccess.ActivityOwns(h,activity) &&
            activity.AccountId==followup.AccountId && activity.LeadId==followup.LeadId &&
            activity.OpportunityId==followup.OpportunityId &&
            await CommercialAccess.SubjectCanRead(db,h,activity.AccountId,
                activity.LeadId,activity.OpportunityId,ct);
    }

    private static async Task<IResult> Complete(Guid id,FollowupCompleteRequest r,
        CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        var x=await db.Followups.FindAsync([id],ct);if(x is null)return Results.NotFound();
        // A linked commitment may be completed by an authorized parent-activity editor.
        if(Manage(h)||Tasks(h))
        {
            if(!await HasAccess(x,db,h,ct))return CrmAccess.Denied();
        }
        else if(!await CanCompleteActivityCommitment(x,db,h,ct))return CrmAccess.Denied();
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
        if(!await HasAccess(x,db,h,ct))return CrmAccess.Denied();
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
    private static async Task<IResult> Archive(Guid id,FollowupCancelRequest r,CrmDbContext db,
        HttpContext h,CancellationToken ct)
    {
        if(!Manage(h) && !Tasks(h))return CrmAccess.Denied();
        var x=await db.Followups.FindAsync([id],ct);
        if(x is null || x.ArchivedAt is not null)return Results.NotFound();
        if(!await HasAccess(x,db,h,ct))return CrmAccess.Denied();
        if(x.RowVersion!=r.ExpectedVersion)return CrmDataEndpoints.Conflict("Concurrent followup update");
        if(string.IsNullOrWhiteSpace(r.Reason)||r.Reason.Length>2000)
            return CrmDataEndpoints.Bad("Archive reason required");
        var actor=CrmAccess.UserId(h);
        if(x.Status is "Pending" or "Overdue")x.Status="Cancelled";
        x.ArchivedAt=DateTime.UtcNow;
        x.UpdatedAt=DateTime.UtcNow;x.UpdatedByUserId=actor;x.RowVersion++;
        db.FollowupHistories.Add(new FollowupHistory
        {
            FollowupId=id,ActorUserId=actor,Action="Archived",
            Reason=r.Reason.Trim(),CreatedByUserId=actor
        });
        var ok=await CrmDataEndpoints.Commit(db,ct);
        return ok is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(x):ok;
    }

    private static async Task<IResult> History(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CanReadCalendar(h))return CrmAccess.Denied();
        var x=await db.Followups.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id,ct);
        if(x is null)return Results.NotFound();
        if(!await HasCalendarAccess(x,db,h,ct))return CrmAccess.Denied();
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
        // Recipient-only reminder visibility. Read-all scopes from another entity type
        // must never expose a seller's private reminder metadata.
        query=query.Where(x=>x.RecipientUserId==CrmAccess.UserId(h));
        if(!string.IsNullOrWhiteSpace(status))query=query.Where(x=>x.Status==status);
        return Results.Ok(await query.OrderByDescending(x=>x.FireAt)
            .Skip((Math.Max(1,page??1)-1)*50).Take(50).ToListAsync(ct));
    }
}
