using Dhole.Crm.Application.Commercial;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

// An explicit, versioned aggregate edit: nothing is silently deleted when omitted.
public sealed record ActivityEditedNote(Guid? Id, long? ExpectedVersion, string NoteText);
public sealed record ActivityEditedCommitment(Guid? Id, long? ExpectedVersion,
    string Subject, DateTime DueAt, bool Completed = false, bool Remove = false);
public sealed record ActivityDetailsRequest(long ExpectedVersion, string Subject, string Type,
    string? LocationText, DateTime? ScheduledStartAt, DateTime? ScheduledEndAt,
    int? ReminderLeadMinutes, string? ReminderChannelStrategy,
    int? RepeatEveryDays, int? ReminderRepeatMinutes,
    IReadOnlyList<ActivityEditedNote>? Notes,
    IReadOnlyList<ActivityEditedCommitment>? Commitments,
    decimal? LocationLatitude = null, decimal? LocationLongitude = null,
    bool UpdateVisitCoordinates = false);

internal static class ActivityDetailsEditing
{
    public static async Task<IResult> Update(Guid id, ActivityDetailsRequest request,
        CrmDbContext db, HttpContext http, IConfigReferenceService config, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.activity.update")) return CrmAccess.Denied();
        var activity = await db.Activities.FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        if (activity is null) return Results.NotFound();
        if (!CommercialAccess.ActivityOwns(http, activity) ||
            (activity.AccountId is { } accountId &&
             !await CrmAccess.CanSeeAccount(db, http, accountId, ct)))
            return CrmAccess.Denied();
        if (activity.RowVersion != request.ExpectedVersion)
            return CommercialAccess.Conflict("Stale activity version. Reload the activity before saving.");

        var notes = request.Notes ?? [];
        var commitments = request.Commitments ?? [];
        if (string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Length > 250 ||
            !CommercialRules.ValidActivityType(request.Type) ||
            !await config.IsActiveCodeAsync("crm-activity-types", request.Type, ct) ||
            !CommercialRules.ValidActivitySchedule(request.ScheduledStartAt, request.ScheduledEndAt) ||
            request.LocationText?.Length > 2000 ||
             (request.UpdateVisitCoordinates &&
              !ActivityVisitLocationRules.IsValid(request.Type,request.LocationLatitude,request.LocationLongitude)) ||
            request.ReminderLeadMinutes is < 0 or > 10080 ||
            (request.RepeatEveryDays.HasValue && request.RepeatEveryDays.Value is not (0 or 1 or 7 or 14 or 30)) ||
            (request.ReminderRepeatMinutes.HasValue && request.ReminderRepeatMinutes.Value is not (0 or 5 or 15 or 30 or 60 or 240 or 1440)) ||
            (request.ReminderChannelStrategy is not null &&
             request.ReminderChannelStrategy is not ("EMAIL" or "SYSTEM" or "BOTH")))
            return CommercialAccess.Invalid("Invalid activity subject, type, schedule or reminder settings.");
        if (notes.Count > 100 || commitments.Count > 100 ||
            notes.Any(x => string.IsNullOrWhiteSpace(x.NoteText) || x.NoteText.Length > 20000 ||
                (x.Id.HasValue && !x.ExpectedVersion.HasValue)) ||
            commitments.Any(x => string.IsNullOrWhiteSpace(x.Subject) || x.Subject.Length > 250 ||
                x.DueAt.Kind != DateTimeKind.Utc ||
                (x.Id.HasValue && !x.ExpectedVersion.HasValue) ||
                (!x.Id.HasValue && (x.Remove || x.DueAt <= DateTime.UtcNow))) ||
            notes.Where(x => x.Id.HasValue).Select(x => x.Id!.Value).Distinct().Count() != notes.Count(x => x.Id.HasValue) ||
            commitments.Where(x => x.Id.HasValue).Select(x => x.Id!.Value).Distinct().Count() != commitments.Count(x => x.Id.HasValue))
            return CommercialAccess.Invalid("Invalid or duplicated meeting minutes or commitments.");

        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct);
        var storedNotes = await db.ActivityNotes.Where(x => x.ActivityId == id).ToListAsync(ct);
        var noteById = storedNotes.ToDictionary(x => x.Id);
        var origins = new List<string> { "Acuerdo registrado en actividad comercial " + id };
        origins.AddRange(storedNotes.Select(x => "Acuerdo de observación " + x.Id));
        var linkedIds = await db.FollowupHistories.AsNoTracking()
            .Where(x => x.Action == "Created" && x.Reason != null && origins.Contains(x.Reason))
            .Select(x => x.FollowupId).Distinct().ToListAsync(ct);
        var storedCommitments = await db.Followups
            .Where(x => linkedIds.Contains(x.Id) && x.ArchivedAt == null &&
                x.AccountId == activity.AccountId && x.LeadId == activity.LeadId &&
                x.OpportunityId == activity.OpportunityId &&
                x.AssignedUserId == activity.OwnerUserId)
            .ToDictionaryAsync(x => x.Id, ct);

        // Validate the whole batch before mutating any tracked entity.
        foreach (var note in notes.Where(x => x.Id.HasValue))
            if (!noteById.TryGetValue(note.Id!.Value, out var found) ||
                found.RowVersion != note.ExpectedVersion ||
                found.NoteText.StartsWith("[Historial de edición]", StringComparison.Ordinal))
                return CommercialAccess.Conflict("Meeting minutes have changed or cannot be edited.");
        foreach (var commitment in commitments.Where(x => x.Id.HasValue))
            if (!storedCommitments.TryGetValue(commitment.Id!.Value, out var found) ||
                found.RowVersion != commitment.ExpectedVersion)
                return CommercialAccess.Conflict("An agreement was changed or no longer belongs to this activity.");

        var now = DateTime.UtcNow;
        var actor = CrmAccess.UserId(http);
        activity.Subject = request.Subject.Trim();
        activity.Type = request.Type;
        activity.LocationText = request.LocationText?.Trim();
        if (request.Type != "VISIT")
        {
            activity.LocationLatitude = null;
            activity.LocationLongitude = null;
        }
        else if (request.UpdateVisitCoordinates)
        {
            activity.LocationLatitude = request.LocationLatitude;
            activity.LocationLongitude = request.LocationLongitude;
        }
        activity.ScheduledStartAt = request.ScheduledStartAt;
        activity.ScheduledEndAt = request.ScheduledEndAt;
        activity.ReminderLeadMinutes = request.ReminderLeadMinutes;
        activity.ReminderChannelStrategy = request.ReminderChannelStrategy;
        activity.RepeatEveryDays = request.RepeatEveryDays is > 0 ? request.RepeatEveryDays : null;
        activity.ReminderRepeatMinutes = request.ReminderRepeatMinutes is > 0 ? request.ReminderRepeatMinutes : null;
        activity.RowVersion++;
        activity.UpdatedAt = now;
        activity.UpdatedByUserId = actor;

        foreach (var change in notes)
        {
            if (change.Id.HasValue)
            {
                var note = noteById[change.Id.Value];
                if (note.NoteText == change.NoteText.Trim()) continue;
                db.ActivityNotes.Add(new ActivityNote {
                    ActivityId = id, AuthorUserId = actor, CreatedByUserId = actor,
                    NoteText = "[Historial de edición] Nota " + note.Id +
                        " actualizada el " + now.ToString("O") +
                        ". Versión anterior:\n" + note.NoteText
                });
                note.NoteText = change.NoteText.Trim();
                note.RowVersion++;
                note.UpdatedAt = now;
                note.UpdatedByUserId = actor;
            }
            else
            {
                db.ActivityNotes.Add(new ActivityNote {
                    ActivityId = id, AuthorUserId = actor, CreatedByUserId = actor,
                    NoteText = change.NoteText.Trim()
                });
            }
        }

        foreach (var change in commitments)
        {
            if (change.Id.HasValue)
            {
                var followup = storedCommitments[change.Id.Value];
                var previousDue = followup.DueAt;
                var previousStatus = followup.Status;
                if (!change.Remove && followup.Subject == change.Subject.Trim() &&
                    followup.DueAt == change.DueAt &&
                    (followup.Status == "Completed") == change.Completed) continue;

                followup.Subject = change.Subject.Trim();
                followup.DueAt = change.DueAt;
                if (change.Remove)
                {
                    followup.ArchivedAt = now;
                    if (followup.Status is "Pending" or "Overdue") followup.Status = "Cancelled";
                }
                else if (change.Completed)
                {
                    followup.Status = "Completed";
                    followup.CompletedAt ??= now;
                    followup.Outcome ??= "Completado desde la actividad comercial";
                }
                else
                {
                    followup.Status = change.DueAt < now ? "Overdue" : "Pending";
                    followup.CompletedAt = null;
                    followup.Outcome = null;
                }
                followup.RowVersion++;
                followup.UpdatedAt = now;
                followup.UpdatedByUserId = actor;
                db.FollowupHistories.Add(new FollowupHistory {
                    FollowupId = followup.Id, ActorUserId = actor, CreatedByUserId = actor,
                    Action = change.Remove ? "Archived" :
                        change.Completed && previousStatus != "Completed" ? "Completed" :
                        !change.Completed && previousStatus == "Completed" ? "Reopened" : "Edited",
                    Reason = "Edición desde actividad comercial " + id +
                        "; estado anterior: " + previousStatus,
                    PreviousDueAt = previousDue, NewDueAt = change.DueAt
                });
                if (!change.Remove && !change.Completed && change.DueAt > now)
                    await CrmReminderEnqueue.QueueAsync(db, "Followup", followup.Id,
                        followup.AccountId, followup.AssignedUserId, followup.RowVersion,
                        followup.DueAt, followup.ReminderLeadMinutes,
                        followup.ReminderChannelStrategy, "crm.followup.reminder.v1",
                        followup.CreatedByUserId, ct);
            }
            else
            {
                var followup = new Followup {
                    AccountId = activity.AccountId, LeadId = activity.LeadId,
                    OpportunityId = activity.OpportunityId, AssignedUserId = activity.OwnerUserId,
                    Channel = "SYSTEM", Subject = change.Subject.Trim(), DueAt = change.DueAt,
                    ReminderLeadMinutes = activity.ReminderLeadMinutes ?? 60,
                    ReminderChannelStrategy = "BOTH", CreatedByUserId = actor,
                    Status = change.Completed ? "Completed" : "Pending",
                    CompletedAt = change.Completed ? now : null
                };
                db.Followups.Add(followup);
                db.FollowupHistories.Add(new FollowupHistory {
                    FollowupId = followup.Id, ActorUserId = actor, CreatedByUserId = actor,
                    Action = "Created", Reason = "Acuerdo registrado en actividad comercial " + id +
                        (activity.ScheduledStartAt.HasValue ? "" : " (tarea de bitácora)"),
                    NewDueAt = followup.DueAt
                });
                if (!change.Completed)
                    await CrmReminderEnqueue.QueueAsync(db, "Followup", followup.Id,
                        followup.AccountId, followup.AssignedUserId, followup.RowVersion,
                        followup.DueAt, followup.ReminderLeadMinutes,
                        followup.ReminderChannelStrategy, "crm.followup.reminder.v1", actor, ct);
            }
        }

        if (activity.Status == "Scheduled" && activity.ScheduledStartAt is { } scheduled &&
            scheduled > now)
            await CrmReminderEnqueue.QueueAsync(db, "Activity", activity.Id,
                activity.AccountId, activity.OwnerUserId, activity.RowVersion, scheduled,
                activity.ReminderLeadMinutes, activity.ReminderChannelStrategy,
                activity.Type == "CALL" ? "crm.call.reminder.v1" :
                activity.Type == "VISIT" ? "crm.visit.reminder.v1" :
                activity.Type == "MEETING" ? "crm.meeting.reminder.v1" :
                "crm.activity.reminder.v1", activity.CreatedByUserId, ct);

        var saved = await CommercialAccess.Save(db, ct);
        if (saved is not Microsoft.AspNetCore.Http.HttpResults.Ok) return saved;
        await transaction.CommitAsync(ct);
        return Results.Ok(activity);
    }
}
