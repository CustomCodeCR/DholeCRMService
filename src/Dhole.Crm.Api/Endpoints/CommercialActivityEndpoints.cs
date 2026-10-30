using Dhole.Crm.Application.Commercial;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;
public sealed record ActivityCreateRequest(Guid? AccountId,Guid? LeadId,Guid? OpportunityId,
    Guid? ContactId,Guid? OwnerUserId,string Type,string Subject,DateTime? ScheduledStartAt,
    DateTime? ScheduledEndAt,DateTime? OccurredAt,string? LocationText);
public sealed record ActivityPatchRequest(long ExpectedVersion,string Subject,string? LocationText);
public sealed record ActivityCompleteRequest(long ExpectedVersion,string Outcome,DateTime OccurredAt,string? Note);
public sealed record ActivityRescheduleRequest(long ExpectedVersion,DateTime ScheduledStartAt,
    DateTime? ScheduledEndAt,string Reason);
public sealed record ActivityCancelRequest(long ExpectedVersion,string Reason);
public sealed record ActivityNoteRequest(string NoteText);
public sealed record ActivityParticipantRequest(Guid? ContactId,Guid? UserId,string? ParticipantName,string Role);

internal static class CommercialActivityEndpoints
{
    public static RouteGroupBuilder MapCommercialActivities(this RouteGroupBuilder g)
    {
        g.MapGet("/activities",List);
        g.MapGet("/activities/{id:guid}",Get);
        g.MapPost("/activities",Create);
        g.MapPatch("/activities/{id:guid}",Patch);
        g.MapPost("/activities/{id:guid}/complete",Complete);
        g.MapPost("/activities/{id:guid}/reschedule",Reschedule);
        g.MapPost("/activities/{id:guid}/cancel",Cancel);
        g.MapPost("/activities/{id:guid}/archive",Archive);
        g.MapGet("/activities/{id:guid}/notes",Notes);
        g.MapPost("/activities/{id:guid}/notes",AddNote);
        g.MapGet("/activities/{id:guid}/participants",Participants);
        g.MapPost("/activities/{id:guid}/participants",AddParticipant);
        return g;
    }
    private static bool CanRead(HttpContext h)=>CrmDataEndpoints.HasRead(h,"activity");
    private static async Task<Activity?> FindAllowed(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        var a=await db.Activities.FirstOrDefaultAsync(x=>x.Id==id&&x.ArchivedAt==null,ct);
        return a is not null && CommercialAccess.ActivityOwns(h,a) &&
            (!a.AccountId.HasValue || await CrmAccess.CanSeeAccount(db,h,a.AccountId.Value,ct)) ? a : null;
    }
    private static async Task<IResult> List(CrmDbContext db,HttpContext h,
        Guid? accountId,Guid? leadId,Guid? opportunityId,string? type,string? status,
        int? page,int? pageSize,CancellationToken ct)
    {
        if(!CanRead(h))return CrmAccess.Denied();
        var visibleAccounts=CrmAccess.VisibleAccounts(db,h).Select(a=>a.Id);
        var query=db.Activities.AsNoTracking().Where(x=>x.ArchivedAt==null &&
            (!x.AccountId.HasValue || visibleAccounts.Contains(x.AccountId.Value)));
        if(!CrmAccess.Scope(h,"crm.activity.read-all"))
            query=query.Where(x=>x.OwnerUserId==CrmAccess.UserId(h));
        if(accountId is { } a)query=query.Where(x=>x.AccountId==a);
        if(leadId is { } l)query=query.Where(x=>x.LeadId==l);
        if(opportunityId is { } o)query=query.Where(x=>x.OpportunityId==o);
        if(!string.IsNullOrWhiteSpace(type))query=query.Where(x=>x.Type==type);
        if(!string.IsNullOrWhiteSpace(status))query=query.Where(x=>x.Status==status);
        var n=Math.Max(1,page??1);var size=Math.Clamp(pageSize??25,1,100);
        var total=await query.CountAsync(ct);
        return Results.Ok(new {total,page=n,pageSize=size,items=await query
            .OrderByDescending(x=>x.OccurredAt??x.ScheduledStartAt??x.CreatedAt).ThenBy(x=>x.Id)
            .Skip((n-1)*size).Take(size).ToListAsync(ct)});
    }
    private static async Task<IResult> Get(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CanRead(h))return CrmAccess.Denied();
        var a=await FindAllowed(id,db,h,ct);
        return a is null?Results.NotFound():Results.Ok(a);
    }
    private static async Task<IResult> Create(ActivityCreateRequest r,CrmDbContext db,HttpContext h,
        IAuthReferenceService auth,IConfigReferenceService config,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.activity.create"))return CrmAccess.Denied();
        if(!CommercialRules.ValidActivityType(r.Type)||
            string.IsNullOrWhiteSpace(r.Subject)||r.Subject.Length>250||
            !CommercialRules.ValidActivitySchedule(r.ScheduledStartAt,r.ScheduledEndAt)||
            (r.OccurredAt.HasValue&&r.OccurredAt.Value.Kind!=DateTimeKind.Utc)||
            !await CommercialAccess.SubjectCanRead(db,h,r.AccountId,r.LeadId,r.OpportunityId,ct)||
            !await CommercialAccess.ValidPartyContact(db,r.ContactId,r.AccountId,r.OpportunityId,ct))
            return CommercialAccess.Invalid("Invalid activity type, schedule or authorized subject");
        var current=CrmAccess.UserId(h);
        var owner=r.OwnerUserId??current;
        if(owner!=current&&!CrmAccess.Scope(h,"crm.activity.read-all"))
            return CrmAccess.Denied();
        if(await auth.GetActiveAsync(owner,ct) is null)return CommercialAccess.Invalid("Activity owner inactive in Auth");
        if(!await config.IsActiveCodeAsync("crm-activity-types",r.Type,ct))
            return CommercialAccess.Invalid("Unknown activity type in Config");
        if(r.OccurredAt is not null&&r.OccurredAt>DateTime.UtcNow)
            return CommercialAccess.Invalid("Cannot record future actual occurrence");
        var activity=new Activity {
            AccountId=r.AccountId,LeadId=r.LeadId,OpportunityId=r.OpportunityId,ContactId=r.ContactId,
            OwnerUserId=owner,Type=r.Type,Subject=r.Subject.Trim(),
            ScheduledStartAt=r.ScheduledStartAt,ScheduledEndAt=r.ScheduledEndAt,
            OccurredAt=r.OccurredAt,Status=r.OccurredAt.HasValue?"Completed":"Scheduled",
            Outcome=r.OccurredAt.HasValue?"COMPLETED":null,
            LocationText=r.LocationText,CreatedByUserId=current
        };
        db.Activities.Add(activity);
        try{await db.SaveChangesAsync(ct);return Results.Created("/api/crm/activities/"+activity.Id,activity);}
        catch(DbUpdateException){return CommercialAccess.Conflict("Activity relation constraint");}
    }
    private static async Task<IResult> Patch(Guid id,ActivityPatchRequest r,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.activity.update"))return CrmAccess.Denied();
        var a=await FindAllowed(id,db,h,ct);if(a is null)return Results.NotFound();
        if(a.RowVersion!=r.ExpectedVersion)return CommercialAccess.Conflict("Stale activity version");
        if(a.Status!="Scheduled")return CommercialAccess.Conflict("Only scheduled activities can be edited");
        if(string.IsNullOrWhiteSpace(r.Subject)||r.Subject.Length>250)
            return CommercialAccess.Invalid("Invalid activity subject");
        a.Subject=r.Subject.Trim();a.LocationText=r.LocationText;
        a.RowVersion++;a.UpdatedAt=DateTime.UtcNow;a.UpdatedByUserId=CrmAccess.UserId(h);
        var result=await CommercialAccess.Save(db,ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(a):result;
    }
    private static async Task<IResult> Complete(Guid id,ActivityCompleteRequest r,CrmDbContext db,HttpContext h,
        IConfigReferenceService config,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.activity.update"))return CrmAccess.Denied();
        var a=await FindAllowed(id,db,h,ct);if(a is null)return Results.NotFound();
        if(a.RowVersion!=r.ExpectedVersion)return CommercialAccess.Conflict("Stale activity version");
        if(a.Status!="Scheduled")return CommercialAccess.Conflict("Activity already finished or cancelled");
        if(r.OccurredAt.Kind!=DateTimeKind.Utc||r.OccurredAt>DateTime.UtcNow||
            string.IsNullOrWhiteSpace(r.Outcome)||!await config.IsActiveCodeAsync("crm-activity-outcomes",r.Outcome,ct))
            return CommercialAccess.Invalid("Invalid occurrence timestamp or outcome");
        a.OccurredAt=r.OccurredAt;a.Outcome=r.Outcome;a.Status="Completed";
        a.RowVersion++;a.UpdatedAt=DateTime.UtcNow;a.UpdatedByUserId=CrmAccess.UserId(h);
        if(!string.IsNullOrWhiteSpace(r.Note))db.ActivityNotes.Add(new ActivityNote {
            ActivityId=id,AuthorUserId=CrmAccess.UserId(h),CreatedByUserId=CrmAccess.UserId(h),
            NoteText=r.Note.Trim()
        });
        var result=await CommercialAccess.Save(db,ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(a):result;
    }
    private static async Task<IResult> Reschedule(Guid id,ActivityRescheduleRequest r,CrmDbContext db,
        HttpContext h,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.activity.update"))return CrmAccess.Denied();
        var a=await FindAllowed(id,db,h,ct);if(a is null)return Results.NotFound();
        if(a.RowVersion!=r.ExpectedVersion)return CommercialAccess.Conflict("Stale activity version");
        if(a.Status!="Scheduled")return CommercialAccess.Conflict("Cannot reschedule completed activity");
        if(string.IsNullOrWhiteSpace(r.Reason)||r.Reason.Length>2000||
            !CommercialRules.ValidActivitySchedule(r.ScheduledStartAt,r.ScheduledEndAt)||
            r.ScheduledStartAt<DateTime.UtcNow)
            return CommercialAccess.Invalid("Invalid future reschedule or missing reason");
        var oldStart=a.ScheduledStartAt;var oldEnd=a.ScheduledEndAt;
        a.ScheduledStartAt=r.ScheduledStartAt;a.ScheduledEndAt=r.ScheduledEndAt;
        a.RowVersion++;a.UpdatedAt=DateTime.UtcNow;a.UpdatedByUserId=CrmAccess.UserId(h);
        db.ActivityNotes.Add(new ActivityNote {ActivityId=id,
            AuthorUserId=CrmAccess.UserId(h),CreatedByUserId=CrmAccess.UserId(h),
            NoteText=$"Rescheduled: {oldStart:O} - {oldEnd:O} -> {r.ScheduledStartAt:O} - {r.ScheduledEndAt:O}; {r.Reason}"});
        var result=await CommercialAccess.Save(db,ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(a):result;
    }
    private static async Task<IResult> Cancel(Guid id,ActivityCancelRequest r,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.activity.update"))return CrmAccess.Denied();
        var a=await FindAllowed(id,db,h,ct);if(a is null)return Results.NotFound();
        if(a.RowVersion!=r.ExpectedVersion)return CommercialAccess.Conflict("Stale activity version");
        if(a.Status!="Scheduled")return CommercialAccess.Conflict("Only scheduled activity can be cancelled");
        if(string.IsNullOrWhiteSpace(r.Reason)||r.Reason.Length>2000)
            return CommercialAccess.Invalid("Cancellation reason required");
        a.Status="Cancelled";a.Outcome="OTHER";a.RowVersion++;
        a.UpdatedAt=DateTime.UtcNow;a.UpdatedByUserId=CrmAccess.UserId(h);
        db.ActivityNotes.Add(new ActivityNote {ActivityId=id,
            AuthorUserId=CrmAccess.UserId(h),CreatedByUserId=CrmAccess.UserId(h),
            NoteText="Cancellation: "+r.Reason.Trim()});
        var result=await CommercialAccess.Save(db,ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(a):result;
    }
    private static async Task<IResult> Archive(Guid id,ActivityCancelRequest r,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.activity.delete"))return CrmAccess.Denied();
        var a=await FindAllowed(id,db,h,ct);if(a is null)return Results.NotFound();
        if(a.RowVersion!=r.ExpectedVersion)return CommercialAccess.Conflict("Stale activity version");
        if(string.IsNullOrWhiteSpace(r.Reason)||r.Reason.Length>2000)
            return CommercialAccess.Invalid("Archive reason required");
        a.ArchivedAt=DateTime.UtcNow;a.UpdatedAt=DateTime.UtcNow;
        a.UpdatedByUserId=CrmAccess.UserId(h);a.RowVersion++;
        db.ActivityNotes.Add(new ActivityNote {ActivityId=id,
            AuthorUserId=CrmAccess.UserId(h),CreatedByUserId=CrmAccess.UserId(h),
            NoteText="Archived: "+r.Reason.Trim()});
        var result=await CommercialAccess.Save(db,ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(a):result;
    }
    private static async Task<IResult> Notes(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CanRead(h)||await FindAllowed(id,db,h,ct) is null)return CrmAccess.Denied();
        return Results.Ok(await db.ActivityNotes.AsNoTracking().Where(x=>x.ActivityId==id)
            .OrderBy(x=>x.CreatedAt).ToListAsync(ct));
    }
    private static async Task<IResult> AddNote(Guid id,ActivityNoteRequest r,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.activity.update")||await FindAllowed(id,db,h,ct) is null)
            return CrmAccess.Denied();
        if(string.IsNullOrWhiteSpace(r.NoteText)||r.NoteText.Length>8000)
            return CommercialAccess.Invalid("Empty or excessive observation");
        var note=new ActivityNote {ActivityId=id,
            AuthorUserId=CrmAccess.UserId(h),CreatedByUserId=CrmAccess.UserId(h),
            NoteText=r.NoteText.Trim()};
        db.ActivityNotes.Add(note);await db.SaveChangesAsync(ct);
        return Results.Created("/api/crm/activities/"+id+"/notes",note);
    }
    private static async Task<IResult> Participants(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CanRead(h)||await FindAllowed(id,db,h,ct) is null)return CrmAccess.Denied();
        return Results.Ok(await db.ActivityParticipants.AsNoTracking().Where(x=>x.ActivityId==id)
            .OrderBy(x=>x.CreatedAt).ToListAsync(ct));
    }
    private static async Task<IResult> AddParticipant(Guid id,ActivityParticipantRequest r,CrmDbContext db,
        HttpContext h,IAuthReferenceService auth,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.activity.update"))return CrmAccess.Denied();
        var a=await FindAllowed(id,db,h,ct);if(a is null)return Results.NotFound();
        var subjects=(r.ContactId.HasValue?1:0)+(r.UserId.HasValue?1:0)+
            (!string.IsNullOrWhiteSpace(r.ParticipantName)?1:0);
        if(subjects!=1||string.IsNullOrWhiteSpace(r.Role)||r.Role.Length>60||
            r.ParticipantName?.Length>240)return CommercialAccess.Invalid("Exactly one participant subject is required");
        if(r.UserId is { } idUser && await auth.GetActiveAsync(idUser,ct) is null)
            return CommercialAccess.Invalid("Internal participant inactive in Auth");
        if(r.ContactId is { } idContact &&
            !await CommercialAccess.ContactBelongsTo(db,idContact,a.AccountId,a.OpportunityId,ct))
            return CommercialAccess.Invalid("Contact not associated with activity account");
        var item=new ActivityParticipant {ActivityId=id,ContactId=r.ContactId,UserId=r.UserId,
            ParticipantName=r.ParticipantName,Role=r.Role,CreatedByUserId=CrmAccess.UserId(h)};
        db.ActivityParticipants.Add(item);await db.SaveChangesAsync(ct);
        return Results.Created("/api/crm/activities/"+id+"/participants",item);
    }
}
