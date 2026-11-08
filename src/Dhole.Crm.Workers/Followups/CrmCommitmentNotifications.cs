using System.Data;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dhole.Crm.Workers.Followups;

/// <summary>
/// Schedules dual-channel commercial commitments without relying on an open browser.
/// A transaction advisory lock plus a unique reminder dedupe key prevent mass resends,
/// even when multiple worker replicas are running.
/// </summary>
internal static class CrmCommitmentNotifications
{
    private const int LookbackDays = 30;
    private const int BatchSize = 500;
    private static readonly int[] ReminderLeads = [1440, 60, 0];

    private sealed record Commitment(
        string EntityType, Guid EntityId, Guid OwnerUserId, long Version,
        DateTime Start, DateTime Deadline, int LeadMinutes,
        int? ReminderRepeatMinutes, Guid CreatedByUserId);

    public static async Task PlanAsync(
        CrmDbContext db, IAuthReferenceService auth, DateTime now,
        ILogger logger, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await CrmReminderEngine.Acquire(db, 5012522, ct)) return;

        var earliest = now.AddDays(-LookbackDays);
        var horizon = now.AddDays(2);
        var activities = await db.Activities.AsNoTracking()
            .Where(a => a.ArchivedAt == null && a.Status == "Scheduled" &&
                a.ScheduledStartAt != null && a.ScheduledStartAt <= horizon &&
                a.ScheduledStartAt >= earliest)
            .OrderByDescending(a => a.ScheduledStartAt).Take(BatchSize).ToListAsync(ct);
        var followups = await db.Followups.AsNoTracking()
            .Where(f => f.ArchivedAt == null &&
                (f.Status == "Pending" || f.Status == "Overdue") &&
                f.DueAt <= horizon && f.DueAt >= earliest)
            .OrderByDescending(f => f.DueAt).Take(BatchSize).ToListAsync(ct);

        var work = activities.Select(a => new Commitment(
                "Activity", a.Id, a.OwnerUserId, a.RowVersion,
                a.ScheduledStartAt!.Value,
                a.ScheduledEndAt ?? a.ScheduledStartAt!.Value,
                Math.Clamp(a.ReminderLeadMinutes ?? 60, 0, 10080),
                a.ReminderRepeatMinutes,a.CreatedByUserId))
            .Concat(followups.Select(f => new Commitment(
                "Followup", f.Id, f.AssignedUserId, f.RowVersion,
                f.DueAt, f.DueAt, Math.Clamp(f.ReminderLeadMinutes ?? 60, 0, 10080),
                null,f.CreatedByUserId))).ToArray();
        if (work.Length == 0) return;

        // Fetch existing keys once rather than issuing a query for each delivery.
        var activityIds = activities.Select(a => a.Id).ToArray();
        var followupIds = followups.Select(f => f.Id).ToArray();
        var existing = (await db.Reminders.AsNoTracking()
            .Where(r => (r.EntityType == "Activity" && activityIds.Contains(r.EntityId)) ||
                (r.EntityType == "Followup" && followupIds.Contains(r.EntityId)))
            .Select(r => r.DedupeKey).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        var escalations = new List<(Commitment Item, int Slot)>();
        foreach (var item in work)
        {
            // Notify 24 hours ahead, 1 hour ahead, and at the scheduled time.
            // The selected per-event lead is retained too; its key is compatible
            // with reminders already enqueued by the API and original scheduler.
            foreach (var lead in ReminderLeads.Append(item.LeadMinutes).Distinct())
            {
                var fireAt = item.Start.AddMinutes(-lead);
                // Tolerate up to two minutes of scheduler delay at the exact start.
                if (fireAt < now.AddMinutes(-2)) continue;
                var phase = lead == item.LeadMinutes ? "lead" :
                    lead == 1440 ? "day" : lead == 60 ? "hour" : "start";
                QueuePair(db, existing, item, item.OwnerUserId, phase,
                    "owner", fireAt, now,
                    phase == "start" ? "crm.commitment.start.v1" :
                    "crm.commitment.upcoming.v1");
            }

            // Send only the current repeat interval while the activity is unresolved.
            if(item.ReminderRepeatMinutes is > 0 && now>=item.Start)
            {
                var slot=(int)((now-item.Start).TotalMinutes/item.ReminderRepeatMinutes.Value);
                if(slot>0)
                {
                    var repeatedAt=item.Start.AddMinutes(slot*item.ReminderRepeatMinutes.Value);
                    if(repeatedAt>=now.AddMinutes(-2))
                        QueuePair(db,existing,item,item.OwnerUserId,"repeat-"+slot,
                            "owner",repeatedAt,now,"crm.commitment.upcoming.v1");
                }
            }

            // A scheduled activity is NOT considered completed until a seller
            // explicitly records its outcome. The deadline is the appointment end.
            if (now < item.Deadline.AddMinutes(30)) continue;
            var slot = Math.Max(0, (int)((now - item.Deadline.AddMinutes(30)).TotalHours / 24));
            QueuePair(db, existing, item, item.OwnerUserId,
                "late-" + slot, "owner", now, now, "crm.commitment.overdue.v1");
            escalations.Add((item, slot));
        }

        // Resolve the escalation recipient in Auth, never by a hardcoded email.
        // Lookup only when a new manager notification is actually necessary.
        if (escalations.Any(x =>
            !existing.Contains(Key(x.Item, "late-" + x.Slot, "manager", "System"))))
        {
            try
            {
                var manager = await auth.GetActiveByUsernameAsync("jmesen", ct);
                if (manager is null)
                    logger.LogWarning("CRM missed-commitment escalation user jmesen was not found or is inactive");
                else
                    foreach (var (item, slot) in escalations)
                    {
                        if (manager.Id == item.OwnerUserId) continue;
                        QueuePair(db, existing, item, manager.Id,
                            "late-" + slot, "manager", now, now,
                            "crm.commitment.escalated.v1");
                    }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // A transient Auth outage must not block the seller's reminders.
                logger.LogWarning(ex, "CRM escalation recipient lookup failed; will retry next cycle");
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static string Key(Commitment item, string phase, string role, string channel) =>
        phase == "lead" && role == "owner"
            ? $"{item.EntityType}:{item.EntityId:N}:{item.Version}:{channel}"
            : $"crmcommit:{item.EntityType}:{item.EntityId:N}:{item.Version}:{phase}:{role}:{channel}";

    private static void QueuePair(CrmDbContext db, HashSet<string> existing,
        Commitment item, Guid recipient, string phase, string role,
        DateTime fireAt, DateTime now, string type)
    {
        foreach (var channel in new[] { "System", "Email" })
        {
            var key = Key(item, phase, role, channel);
            if (!existing.Add(key)) continue;
            var reminder = new CrmReminder
            {
                EntityType = item.EntityType,
                EntityId = item.EntityId,
                RecipientUserId = recipient,
                Channel = channel,
                NotificationType = type,
                DedupeKey = key,
                TargetVersion = item.Version,
                FireAt = fireAt,
                CreatedByUserId = item.CreatedByUserId
            };
            db.Reminders.Add(reminder);
            db.OutboxMessages.Add(new CrmOutboxMessage
            {
                Id = reminder.Id, ReminderId = reminder.Id,
                NextAttemptAt = fireAt > now ? fireAt : now
            });
        }
    }
}
