using Dhole.Crm.Application.Commercial;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;
public sealed record AgreementCommitmentRequest(string Subject, DateTime DueAt);
public sealed record ActivityCreateRequest(Guid? AccountId,Guid? LeadId,Guid? OpportunityId,
    Guid? ContactId,Guid? OwnerUserId,string Type,string Subject,DateTime? ScheduledStartAt,
    DateTime? ScheduledEndAt,DateTime? OccurredAt,string? LocationText,
    int? ReminderLeadMinutes = null,string? ReminderChannelStrategy = null,
    string? Involved = null,string? Agreements = null,
    string? NextCommitment = null,DateTime? CommitmentDueAt = null,
    IReadOnlyList<AgreementCommitmentRequest>? AgreementCommitments = null,
    int? RepeatEveryDays = null, int? ReminderRepeatMinutes = null,
    string? Minutes = null, IReadOnlyList<Guid>? ParticipantContactIds = null);
public sealed record ActivityPatchRequest(long ExpectedVersion,string Subject,string? LocationText,
    int? RepeatEveryDays = null,int? ReminderLeadMinutes = null,int? ReminderRepeatMinutes = null);
public sealed record ActivityCompleteRequest(long ExpectedVersion,string Outcome,DateTime OccurredAt,string? Note);
public sealed record ActivityRescheduleRequest(long ExpectedVersion,DateTime ScheduledStartAt,
    DateTime? ScheduledEndAt,string Reason);
public sealed record ActivityCancelRequest(long ExpectedVersion,string Reason);
public sealed record ActivityNoteRequest(string NoteText,
    IReadOnlyList<AgreementCommitmentRequest>? AgreementCommitments = null);
public sealed record ExistingAgreementRequest(IReadOnlyList<AgreementCommitmentRequest> Commitments);
public sealed record ActivityParticipantRequest(Guid? ContactId,Guid? UserId,string? ParticipantName,string Role);

internal static class CommercialActivityEndpoints
{
    public static RouteGroupBuilder MapCommercialActivities(this RouteGroupBuilder g)
    {
        g.MapGet("/activities",List);
        g.MapGet("/activities/{id:guid}",Get);
        g.MapPost("/activities",Create);
        g.MapPatch("/activities/{id:guid}",Patch);
        g.MapPut("/activities/{id:guid}/details",ActivityDetailsEditing.Update);
        g.MapPost("/activities/{id:guid}/complete",Complete);
        g.MapPost("/activities/{id:guid}/reschedule",Reschedule);
        g.MapPost("/activities/{id:guid}/cancel",Cancel);
        g.MapPost("/activities/{id:guid}/archive",Archive);

        g.MapGet("/activities/{id:guid}/commitments",Commitments);
        g.MapGet("/activities/{id:guid}/notes",Notes);
        g.MapPost("/activities/{id:guid}/notes",AddNote);
        g.MapPost("/activities/{id:guid}/notes/{noteId:guid}/commitments",ScheduleExistingNote);
        g.MapGet("/activities/{id:guid}/participants",Participants);
        g.MapPost("/activities/{id:guid}/participants",AddParticipant);
        return g;
    }
    private static bool CanRead(HttpContext h)=>CrmDataEndpoints.HasRead(h,"activity") || CrmAccess.CanManageAgendaAcrossUsers(h);
    private static async Task<Activity?> FindAllowed(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        var a=await db.Activities.FirstOrDefaultAsync(x=>x.Id==id&&x.ArchivedAt==null,ct);
        return a is not null && CommercialAccess.ActivityOwns(h,a) &&
            (!a.AccountId.HasValue || await CrmAccess.CanSeeAccount(db,h,a.AccountId.Value,ct)) ? a : null;
    }
    private static async Task<IResult> List(CrmDbContext db,HttpContext h,
        Guid? accountId,Guid? leadId,Guid? opportunityId,Guid? ownerId,string? type,string? status,
        DateTime? from,DateTime? to,int? page,int? pageSize,CancellationToken ct)
    {
        if(!CanRead(h))return CrmAccess.Denied();
        var visibleAccounts=CrmAccess.VisibleAccounts(db,h).Select(a=>a.Id);
        var query=db.Activities.AsNoTracking().Where(x=>x.ArchivedAt==null &&
            (!x.AccountId.HasValue || visibleAccounts.Contains(x.AccountId.Value)));
        if(!CrmAccess.CanManageAgendaAcrossUsers(h))
            query=query.Where(x=>x.OwnerUserId==CrmAccess.UserId(h));
        if(ownerId is { } owner) {
            if(owner!=CrmAccess.UserId(h) && !CrmAccess.CanManageAgendaAcrossUsers(h))
                return CrmAccess.Denied();
            query=query.Where(x=>x.OwnerUserId==owner);
        }
        if(accountId is { } a)query=query.Where(x=>x.AccountId==a);
        if(leadId is { } l)query=query.Where(x=>x.LeadId==l);
        if(opportunityId is { } o)query=query.Where(x=>x.OpportunityId==o);
        if(!string.IsNullOrWhiteSpace(type))query=query.Where(x=>x.Type==type);
        if(!string.IsNullOrWhiteSpace(status))query=query.Where(x=>x.Status==status);
        if(from.HasValue)query=query.Where(x=>(x.ScheduledStartAt??x.OccurredAt??x.CreatedAt)>=from.Value);
        if(to.HasValue)query=query.Where(x=>(x.ScheduledStartAt??x.OccurredAt??x.CreatedAt)<to.Value);
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
        if((r.ReminderLeadMinutes is < 0 or > 10080) ||
            (r.RepeatEveryDays.HasValue && r.RepeatEveryDays.Value is not (0 or 1 or 7 or 14 or 30)) ||
            (r.ReminderRepeatMinutes.HasValue && r.ReminderRepeatMinutes.Value is not (0 or 5 or 15 or 30 or 60 or 240 or 1440)) ||
            (r.ReminderChannelStrategy is not null && r.ReminderChannelStrategy is not ("BOTH" or "EMAIL" or "SYSTEM"))||
            !CommercialRules.ValidActivityType(r.Type)||
            string.IsNullOrWhiteSpace(r.Subject)||r.Subject.Length>250||
            !CommercialRules.ValidActivitySchedule(r.ScheduledStartAt,r.ScheduledEndAt)||
            (r.OccurredAt.HasValue&&r.OccurredAt.Value.Kind!=DateTimeKind.Utc)||
            !await CommercialAccess.SubjectCanRead(db,h,r.AccountId,r.LeadId,r.OpportunityId,ct)||
            !await CommercialAccess.ValidPartyContact(db,r.ContactId,r.AccountId,r.OpportunityId,ct))
            return CommercialAccess.Invalid("Invalid activity type, schedule or authorized subject");
        var current=CrmAccess.UserId(h);
        var owner=r.OwnerUserId??current;
        if(owner!=current&&!CrmAccess.CanManageAgendaAcrossUsers(h))
            return CrmAccess.Denied();
        var activeOwner=await auth.GetActiveAsync(owner,ct);
        if(activeOwner is null)return CommercialAccess.Invalid("Activity owner inactive in Auth");
        if(!CrmAccess.CanOwnAgenda(activeOwner))
            return CommercialAccess.Invalid("Activity owner requires crm.activity.read, crm.activity.read-all or crm.followup.manage");
        if(!await config.IsActiveCodeAsync("crm-activity-types",r.Type,ct))
            return CommercialAccess.Invalid("Unknown activity type in Config");
        if(r.OccurredAt is not null&&r.OccurredAt>DateTime.UtcNow)
            return CommercialAccess.Invalid("Cannot record future actual occurrence");
        if(r.Involved?.Length>1500 || r.Minutes?.Length>20000 || r.Agreements?.Length>6000 ||
            r.NextCommitment?.Length>250 ||
            (string.IsNullOrWhiteSpace(r.NextCommitment) != !r.CommitmentDueAt.HasValue) ||
            (r.CommitmentDueAt is { } commitmentDue &&
                (commitmentDue.Kind!=DateTimeKind.Utc ||
                 commitmentDue<=DateTime.UtcNow ||
                 (r.ScheduledEndAt is { } scheduledEnd && commitmentDue<scheduledEnd))))
            return CommercialAccess.Invalid("Invalid meeting recap or next commitment deadline");
        var extracted = r.AgreementCommitments ?? [];
        if(extracted.Count > 30 ||
            extracted.Any(x => string.IsNullOrWhiteSpace(x.Subject) ||
                x.Subject.Length > 250 ||
                x.DueAt.Kind != DateTimeKind.Utc ||
                x.DueAt < DateTime.UtcNow ||
                (r.ScheduledStartAt.HasValue && x.DueAt < r.ScheduledStartAt.Value)))
            return CommercialAccess.Invalid("Agreement commitments require a title and a valid future date.");
        var contactIds = r.ParticipantContactIds?.Distinct().ToArray() ?? [];
        if(contactIds.Length>50 || contactIds.Any(x=>x==Guid.Empty))
            return CommercialAccess.Invalid("Too many or invalid activity contacts");
        foreach(var contactId in contactIds)
            if(!await CommercialAccess.ContactBelongsTo(db,contactId,r.AccountId,r.OpportunityId,ct))
                return CommercialAccess.Invalid("Participant contact does not belong to this account");
        var createdAtUtc=DateTime.UtcNow;
        var occurredAt=r.OccurredAt ??
            (CommercialRules.IsHistoricalActivity(r.ScheduledEndAt,createdAtUtc)
                ? r.ScheduledEndAt : null);
        var activity=new Activity {
            AccountId=r.AccountId,LeadId=r.LeadId,OpportunityId=r.OpportunityId,ContactId=r.ContactId,
            OwnerUserId=owner,Type=r.Type,Subject=r.Subject.Trim(),
            ScheduledStartAt=r.ScheduledStartAt,ScheduledEndAt=r.ScheduledEndAt,
            OccurredAt=occurredAt,Status=occurredAt.HasValue?"Completed":"Scheduled",
            Outcome=occurredAt.HasValue?"COMPLETED":null,
            LocationText=r.LocationText,CreatedByUserId=current,
            ReminderLeadMinutes=r.ReminderLeadMinutes,ReminderChannelStrategy=r.ReminderChannelStrategy,
            RepeatEveryDays=r.RepeatEveryDays is > 0 ? r.RepeatEveryDays : null,
            ReminderRepeatMinutes=r.ReminderRepeatMinutes is > 0 ? r.ReminderRepeatMinutes : null
        };
        db.Activities.Add(activity);
        foreach(var contactId in contactIds)
            db.ActivityParticipants.Add(new ActivityParticipant {
                ActivityId=activity.Id,ContactId=contactId,Role="ATTENDEE",
                CreatedByUserId=current
            });
        if(!string.IsNullOrWhiteSpace(r.Minutes))
            db.ActivityNotes.Add(new ActivityNote {
                ActivityId=activity.Id,AuthorUserId=current,CreatedByUserId=current,
                NoteText="Minuta:\n"+r.Minutes.Trim()
            });
        // Reuse the existing notes/participants and followup models rather than
        // storing agreements only in the browser. Save everything atomically.
        var summary=string.Join("\n\n",new[] {
            string.IsNullOrWhiteSpace(r.Involved)?null:"Involucrados: "+r.Involved.Trim(),
            string.IsNullOrWhiteSpace(r.Agreements)?null:"Acuerdos: "+r.Agreements.Trim(),
            string.IsNullOrWhiteSpace(r.NextCommitment)?null:
                "Próximo compromiso: "+r.NextCommitment.Trim()+
                " (vencimiento UTC: "+r.CommitmentDueAt!.Value.ToString("O")+")"
        }.Where(x=>x is not null));
        if(summary.Length>0)
            db.ActivityNotes.Add(new ActivityNote {
                ActivityId=activity.Id,AuthorUserId=current,
                CreatedByUserId=current,NoteText=summary
            });
        // Every confirmed agreement is a separate, account-linked calendar
        // commitment. Preserve original meeting notes as the audit context.
        var commitments = extracted
            .Select(x => (Subject:x.Subject.Trim(), x.DueAt))
            .ToList();
        if(!string.IsNullOrWhiteSpace(r.NextCommitment) && r.CommitmentDueAt.HasValue)
            commitments.Add((r.NextCommitment.Trim(), r.CommitmentDueAt.Value));

        var nextCommitments = new List<Followup>();
        foreach(var item in commitments.DistinctBy(x =>
            (x.Subject.ToUpperInvariant(), x.DueAt)))
        {
            var followup = new Followup {
                AccountId=activity.AccountId,LeadId=activity.LeadId,
                OpportunityId=activity.OpportunityId,AssignedUserId=owner,
                Channel="SYSTEM", Subject=item.Subject,DueAt=item.DueAt,
                ReminderLeadMinutes=r.ReminderLeadMinutes,
                ReminderChannelStrategy="BOTH",CreatedByUserId=current
            };
            nextCommitments.Add(followup);
            db.Followups.Add(followup);
            db.FollowupHistories.Add(new FollowupHistory {
                FollowupId=followup.Id, ActorUserId=current,
                Action="Created", Reason="Acuerdo registrado en actividad comercial "+activity.Id,
                NewDueAt=followup.DueAt,CreatedByUserId=current
            });
        }
        try
        {
            if(activity.Status=="Scheduled" && activity.ScheduledStartAt is { } scheduled &&
                scheduled > DateTime.UtcNow)
                await CrmReminderEnqueue.QueueAsync(db,"Activity",activity.Id,activity.AccountId,
                    activity.OwnerUserId,activity.RowVersion,scheduled,
                    activity.ReminderLeadMinutes,activity.ReminderChannelStrategy,
                    activity.Type=="CALL"?"crm.call.reminder.v1":
                    activity.Type=="VISIT"?"crm.visit.reminder.v1":
                    activity.Type=="MEETING"?"crm.meeting.reminder.v1":"crm.activity.reminder.v1",
                    activity.CreatedByUserId,ct);
            foreach(var followup in nextCommitments)
                await CrmReminderEnqueue.QueueAsync(db,"Followup",followup.Id,
                    followup.AccountId,followup.AssignedUserId,
                    followup.RowVersion,followup.DueAt,
                    followup.ReminderLeadMinutes,"BOTH",
                    "crm.followup.reminder.v1",followup.CreatedByUserId,ct);
            await db.SaveChangesAsync(ct);
            return Results.Created("/api/crm/activities/"+activity.Id,activity);
        }
        catch(DbUpdateException){return CommercialAccess.Conflict("Activity relation constraint");}
    }
    private static async Task<IResult> Patch(Guid id,ActivityPatchRequest r,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.activity.update"))return CrmAccess.Denied();
        var a=await FindAllowed(id,db,h,ct);if(a is null)return Results.NotFound();
        if(a.RowVersion!=r.ExpectedVersion)return CommercialAccess.Conflict("Stale activity version");
        if(a.Status!="Scheduled")return CommercialAccess.Conflict("Only scheduled activities can be edited");
        if(string.IsNullOrWhiteSpace(r.Subject)||r.Subject.Length>250 ||
            r.ReminderLeadMinutes is < 0 or > 10080 ||
            (r.RepeatEveryDays.HasValue && r.RepeatEveryDays.Value is not (0 or 1 or 7 or 14 or 30)) ||
            (r.ReminderRepeatMinutes.HasValue && r.ReminderRepeatMinutes.Value is not (0 or 5 or 15 or 30 or 60 or 240 or 1440)))
            return CommercialAccess.Invalid("Invalid activity subject or reminder recurrence");
        a.Subject=r.Subject.Trim();a.LocationText=r.LocationText;
        if(r.ReminderLeadMinutes.HasValue)a.ReminderLeadMinutes=r.ReminderLeadMinutes;
        if(r.RepeatEveryDays.HasValue)a.RepeatEveryDays=r.RepeatEveryDays is > 0 ? r.RepeatEveryDays : null;
        if(r.ReminderRepeatMinutes.HasValue)a.ReminderRepeatMinutes=r.ReminderRepeatMinutes is > 0 ? r.ReminderRepeatMinutes : null;
        a.RowVersion++;a.UpdatedAt=DateTime.UtcNow;a.UpdatedByUserId=CrmAccess.UserId(h);
        if(a.ScheduledStartAt is { } upcoming && upcoming>DateTime.UtcNow.AddMinutes(-2))
            await CrmReminderEnqueue.QueueAsync(db,"Activity",a.Id,a.AccountId,
                a.OwnerUserId,a.RowVersion,upcoming,a.ReminderLeadMinutes,
                a.ReminderChannelStrategy,a.Type=="CALL"?"crm.call.reminder.v1":
                a.Type=="VISIT"?"crm.visit.reminder.v1":
                a.Type=="MEETING"?"crm.meeting.reminder.v1":"crm.activity.reminder.v1",
                a.CreatedByUserId,ct);
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
        await CrmReminderEnqueue.QueueAsync(db,"Activity",a.Id,a.AccountId,
            a.OwnerUserId,a.RowVersion,r.ScheduledStartAt,a.ReminderLeadMinutes,
            a.ReminderChannelStrategy,a.Type=="CALL"?"crm.call.reminder.v1":
            a.Type=="VISIT"?"crm.visit.reminder.v1":
            a.Type=="MEETING"?"crm.meeting.reminder.v1":"crm.activity.reminder.v1",
            a.CreatedByUserId,ct);
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

    // A dated agreement remains a Followup. Reconstruct the relation from the
    // original creation history so historical records need no data migration.
    private static async Task<IResult> Commitments(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CanRead(h))return CrmAccess.Denied();
        var activity=await FindAllowed(id,db,h,ct);
        if(activity is null)return Results.NotFound();
        var noteIds=await db.ActivityNotes.AsNoTracking()
            .Where(x=>x.ActivityId==id).Select(x=>x.Id).ToListAsync(ct);
        var origins=new List<string>{"Acuerdo registrado en actividad comercial "+id};
        origins.AddRange(noteIds.Select(noteId=>"Acuerdo de observación "+noteId));
        var followupIds=db.FollowupHistories.AsNoTracking()
            .Where(x=>x.Action=="Created" && x.Reason!=null && origins.Contains(x.Reason))
            .Select(x=>x.FollowupId);
        var commitments=await db.Followups.AsNoTracking()
            .Where(x=>followupIds.Contains(x.Id) && x.ArchivedAt==null &&
                x.AccountId==activity.AccountId && x.LeadId==activity.LeadId &&
                x.OpportunityId==activity.OpportunityId &&
                x.AssignedUserId==activity.OwnerUserId)
            .OrderBy(x=>x.DueAt).ThenBy(x=>x.Id)
            .Select(x=>new{x.Id,x.Subject,x.Status,x.DueAt,x.Channel,x.RowVersion})
            .ToListAsync(ct);
        return Results.Ok(commitments);
    }

    private static async Task<IResult> Notes(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CanRead(h)||await FindAllowed(id,db,h,ct) is null)return CrmAccess.Denied();
        return Results.Ok(await db.ActivityNotes.AsNoTracking().Where(x=>x.ActivityId==id)
            .OrderBy(x=>x.CreatedAt).ToListAsync(ct));
    }
    private static async Task<IResult> AddNote(Guid id,ActivityNoteRequest r,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.activity.update"))return CrmAccess.Denied();
        var activity=await FindAllowed(id,db,h,ct);
        if(activity is null)return Results.NotFound();
        if(string.IsNullOrWhiteSpace(r.NoteText)||r.NoteText.Length>8000)
            return CommercialAccess.Invalid("Empty or excessive observation");
        var agreed=r.AgreementCommitments ?? [];
        if(agreed.Count>30 || agreed.Any(x=>string.IsNullOrWhiteSpace(x.Subject) ||
            x.Subject.Length>250 || x.DueAt.Kind!=DateTimeKind.Utc ||
            x.DueAt<DateTime.UtcNow))
            return CommercialAccess.Invalid("Each agreed commitment requires a title and future deadline.");
        var actor=CrmAccess.UserId(h);
        var note=new ActivityNote {ActivityId=id,
            AuthorUserId=actor,CreatedByUserId=actor,NoteText=r.NoteText.Trim()};
        db.ActivityNotes.Add(note);
        var generated=new List<Followup>();
        foreach(var item in agreed.DistinctBy(x=>(x.Subject.Trim().ToUpperInvariant(),x.DueAt)))
        {
            var followup=new Followup {
                AccountId=activity.AccountId,LeadId=activity.LeadId,
                OpportunityId=activity.OpportunityId,
                AssignedUserId=activity.OwnerUserId,Channel="SYSTEM",
                Subject=item.Subject.Trim(),DueAt=item.DueAt,
                ReminderLeadMinutes=activity.ReminderLeadMinutes,
                ReminderChannelStrategy="BOTH",CreatedByUserId=actor
            };
            generated.Add(followup);
            db.Followups.Add(followup);
            db.FollowupHistories.Add(new FollowupHistory {
                FollowupId=followup.Id,ActorUserId=actor,CreatedByUserId=actor,
                Action="Created",Reason="Acuerdo de observación "+note.Id,
                NewDueAt=item.DueAt
            });
            await CrmReminderEnqueue.QueueAsync(db,"Followup",followup.Id,
                followup.AccountId,followup.AssignedUserId,followup.RowVersion,
                followup.DueAt,followup.ReminderLeadMinutes,"BOTH",
                "crm.followup.reminder.v1",actor,ct);
        }
        // Notes and their dated commitments must succeed or fail together.
        var result=await CommercialAccess.Save(db,ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok
            ? Results.Created("/api/crm/activities/"+id+"/notes",
                new {note.Id,note.ActivityId,note.NoteText,note.CreatedAt,
                    createdCommitments=generated.Select(x=>new {x.Id,x.Subject,x.DueAt})})
            : result;
    }
    // Explicit one-time conversion of a note that was saved before dated
    // agreement scheduling existed. The original note and activity stay intact.
    private static async Task<IResult> ScheduleExistingNote(Guid id,Guid noteId,
        ExistingAgreementRequest request,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.activity.update"))return CrmAccess.Denied();
        var activity=await FindAllowed(id,db,h,ct);
        if(activity is null)return Results.NotFound();
        var note=await db.ActivityNotes.AsNoTracking()
            .FirstOrDefaultAsync(x=>x.Id==noteId && x.ActivityId==id,ct);
        if(note is null)return Results.NotFound();
        if(note.NoteText.StartsWith("[Historial de edición]",StringComparison.Ordinal))
            return CommercialAccess.Invalid("Audit history cannot create new agreement commitments.");
        var entries=request.Commitments;
        if(entries is null || entries.Count is < 1 or > 30 ||
            entries.Any(x=>string.IsNullOrWhiteSpace(x.Subject) ||
                x.Subject.Length>250 || x.DueAt.Kind!=DateTimeKind.Utc ||
                x.DueAt<=DateTime.UtcNow))
            return CommercialAccess.Invalid("Confirmed future agreement dates and titles are required.");
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
        var existingReason="Acuerdo de observación "+noteId;
        var exists=await db.FollowupHistories.AsNoTracking()
            .AnyAsync(x=>x.Reason==existingReason,ct);
        if(exists)return CommercialAccess.Conflict("This observation already has calendar commitments.");
        var actor=CrmAccess.UserId(h);
        var generated=new List<Followup>();
        foreach(var item in entries.DistinctBy(x=>(x.Subject.Trim().ToUpperInvariant(),x.DueAt)))
        {
            var followup=new Followup {
                AccountId=activity.AccountId,LeadId=activity.LeadId,
                OpportunityId=activity.OpportunityId,
                AssignedUserId=activity.OwnerUserId,
                Channel="SYSTEM",Subject=item.Subject.Trim(),
                DueAt=item.DueAt,ReminderLeadMinutes=activity.ReminderLeadMinutes,
                ReminderChannelStrategy="BOTH",CreatedByUserId=actor
            };
            generated.Add(followup);
            db.Followups.Add(followup);
            db.FollowupHistories.Add(new FollowupHistory {
                FollowupId=followup.Id,ActorUserId=actor,CreatedByUserId=actor,
                Action="Created",Reason=existingReason,NewDueAt=item.DueAt
            });
            await CrmReminderEnqueue.QueueAsync(db,"Followup",followup.Id,
                followup.AccountId,followup.AssignedUserId,followup.RowVersion,
                followup.DueAt,followup.ReminderLeadMinutes,"BOTH",
                "crm.followup.reminder.v1",actor,ct);
        }
        var committed=await CommercialAccess.Save(db,ct);
        if(committed is not Microsoft.AspNetCore.Http.HttpResults.Ok)return committed;
        await tx.CommitAsync(ct);
        return Results.Created("/api/crm/activities/"+id+"/notes/"+noteId+"/commitments",
            new{noteId,created=generated.Count});
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
