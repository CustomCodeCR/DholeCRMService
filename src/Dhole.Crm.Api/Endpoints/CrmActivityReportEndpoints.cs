using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

// Read-only reporting over the same authorized activities and followups used by Agenda.
// Completed unscheduled commercial notes are activities, not a second countable record.
internal static class CrmActivityReportEndpoints
{
    private sealed record Entry(Guid Id, string Source, Guid OwnerUserId, Guid? AccountId,
        Guid? LeadId, string Type, string Subject, string Status, DateTime At);

    public static RouteGroupBuilder MapActivityReports(this RouteGroupBuilder g)
    {
        g.MapGet("/activities/report", Report);
        return g;
    }

    private static bool Overdue(Entry row, DateTime now) =>
        row.Status == "Overdue" ||
        (row.Status is "Scheduled" or "Pending" && row.At < now);

    private static object Totals(IEnumerable<Entry> source, DateTime now)
    {
        var records = source.ToArray();
        var completed = records.Count(r => r.Status == "Completed");
        return new
        {
            total = records.Length,
            completed,
            pending = records.Count(r => r.Status is "Scheduled" or "Pending" && !Overdue(r, now)),
            overdue = records.Count(r => Overdue(r, now)),
            cancelled = records.Count(r => r.Status == "Cancelled"),
            completionRate = records.Length == 0 ? 0m : Math.Round(completed * 100m / records.Length, 1)
        };
    }

    private static async Task<IResult> Report(CrmDbContext db, HttpContext h,
        DateTime? from, DateTime? to, Guid? ownerId, string? source, string? type,
        string? status, CancellationToken ct)
    {
        var actor = CrmAccess.UserId(h);
        var global = CrmAccess.CanManageAgendaAcrossUsers(h);
        var readActivity = CrmDataEndpoints.HasRead(h, "activity") || global;
        var readLog = CrmAccess.Scope(h, "crm.bitacora.read") ||
            CrmAccess.Scope(h, "crm.bitacora.read-all") || readActivity;
        var globalLog = CrmAccess.Scope(h, "crm.bitacora.read-all") || global;
        var readFollowups = CrmAccess.Scope(h, "crm.task.manage") ||
            CrmAccess.Scope(h, "crm.followup.manage") || readActivity ||
            CrmAccess.Scope(h, "crm.account.read-all");

        if (!readLog && !readFollowups) return CrmAccess.Denied();
        if (source is not (null or "" or "activities" or "followups"))
            return Results.BadRequest(new { message = "Origen de gestiones no válido" });
        if (source == "activities" && !readLog || source == "followups" && !readFollowups)
            return CrmAccess.Denied();
        if (ownerId.HasValue && ownerId.Value != actor && !global &&
            !(source == "activities" && globalLog)) return CrmAccess.Denied();

        var now = DateTime.UtcNow;
        var start = from ?? now.Date.AddDays(-30);
        var end = to ?? now.Date.AddDays(1);
        if (start.Kind != DateTimeKind.Utc || end.Kind != DateTimeKind.Utc ||
            end <= start || end - start > TimeSpan.FromDays(366))
            return Results.BadRequest(new { message = "Seleccione un rango UTC de 1 a 366 días" });

        var visibleAccounts = CrmAccess.VisibleAccounts(db, h).Select(a => a.Id);
        var visibleLeads = db.Leads.AsNoTracking()
            .Where(l => l.ArchivedAt == null &&
                (CrmAccess.Scope(h, "crm.lead.read-all") || l.ExecutiveUserId == actor))
            .Select(l => l.Id);
        var rows = new List<Entry>();

        if (readLog && source != "followups")
        {
            var activities = db.Activities.AsNoTracking().Where(a =>
                a.ArchivedAt == null &&
                (!a.AccountId.HasValue || visibleAccounts.Contains(a.AccountId.Value)) &&
                (!a.LeadId.HasValue || visibleLeads.Contains(a.LeadId.Value)) &&
                (a.OccurredAt ?? a.ScheduledStartAt ?? a.CreatedAt) >= start &&
                (a.OccurredAt ?? a.ScheduledStartAt ?? a.CreatedAt) < end);
            if (!readActivity) activities = activities.Where(a => a.Status == "Completed");
            if (!global) activities = activities.Where(a =>
                a.OwnerUserId == actor || (globalLog && a.Status == "Completed"));
            if (ownerId.HasValue) activities = activities.Where(a => a.OwnerUserId == ownerId.Value);
            if (!string.IsNullOrWhiteSpace(type)) activities = type == "FOLLOWUP"
                ? activities.Where(a => false) : activities.Where(a => a.Type == type);
            if (status == "Overdue") activities = activities.Where(a =>
                a.Status == "Overdue" ||
                ((a.Status == "Scheduled" || a.Status == "Pending") &&
                 (a.OccurredAt ?? a.ScheduledStartAt ?? a.CreatedAt) < now));
            else if (!string.IsNullOrWhiteSpace(status)) activities = activities.Where(a => a.Status == status);
            rows.AddRange(await activities.Select(a => new Entry(a.Id, "activities",
                a.OwnerUserId, a.AccountId, a.LeadId, a.Type, a.Subject, a.Status,
                a.OccurredAt ?? a.ScheduledStartAt ?? a.CreatedAt)).ToListAsync(ct));
        }

        if (readFollowups && source != "activities" &&
            (string.IsNullOrWhiteSpace(type) || type == "FOLLOWUP"))
        {
            var followups = db.Followups.AsNoTracking().Where(f =>
                f.ArchivedAt == null &&
                (!f.AccountId.HasValue || visibleAccounts.Contains(f.AccountId.Value)) &&
                (!f.LeadId.HasValue || visibleLeads.Contains(f.LeadId.Value)) &&
                f.DueAt >= start && f.DueAt < end);
            if (!global) followups = followups.Where(f => f.AssignedUserId == actor);
            if (ownerId.HasValue) followups = followups.Where(f => f.AssignedUserId == ownerId.Value);
            if (status == "Overdue") followups = followups.Where(f =>
                f.Status == "Overdue" ||
                ((f.Status == "Scheduled" || f.Status == "Pending") && f.DueAt < now));
            else if (!string.IsNullOrWhiteSpace(status)) followups = followups.Where(f => f.Status == status);
            rows.AddRange(await followups.Select(f => new Entry(f.Id, "followups",
                f.AssignedUserId, f.AccountId, f.LeadId, "FOLLOWUP",
                f.Subject ?? "Seguimiento comercial", f.Status, f.DueAt)).ToListAsync(ct));
        }

        var ordered = rows.OrderByDescending(r => r.At).ThenBy(r => r.Id).ToArray();
        return Results.Ok(new
        {
            from = start, to = end, allSellers = global,
            summary = Totals(ordered, now),
            sellers = ordered.GroupBy(r => r.OwnerUserId)
                .Select(g => new { ownerUserId = g.Key, count = g.Count(), summary = Totals(g, now) })
                .OrderByDescending(g => g.count).ToArray(),
            types = ordered.GroupBy(r => r.Type)
                .Select(g => new { type = g.Key, count = g.Count() })
                .OrderByDescending(g => g.count).ToArray(),
            daily = ordered.GroupBy(r => r.At.Date)
                .Select(g => new { date = g.Key.ToString("yyyy-MM-dd"), total = g.Count(),
                    completed = g.Count(r => r.Status == "Completed") })
                .OrderBy(d => d.date).ToArray(),
            items = ordered
        });
    }
}
