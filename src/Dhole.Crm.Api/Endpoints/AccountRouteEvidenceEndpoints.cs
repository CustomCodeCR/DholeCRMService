using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

public sealed record AddAccountRouteEvidenceRequest(
    string SourceKind, Guid SourceId, string PolCode, string? PoeCode,
    string PodCode, string? ServiceCode, string? Notes);

/// <summary>
/// Historical route observations are tied to a verified CRM meeting or a quote
/// explicitly linked to the account's opportunity. Quote routes are NOT inferred
/// from a shipment mode or a client name.
/// </summary>
internal static class AccountRouteEvidenceEndpoints
{
    public static RouteGroupBuilder MapAccountRouteEvidence(this RouteGroupBuilder g)
    {
        g.MapGet("/accounts/{id:guid}/route-history", List);
        g.MapPost("/accounts/{id:guid}/route-history", Add);
        return g;
    }

    private static async Task<bool> CanSeeAccount(CrmDbContext db, HttpContext h,
        Guid id, CancellationToken ct) =>
        await db.Accounts.AsNoTracking().AnyAsync(x => x.Id == id && x.ArchivedAt == null &&
            (x.ExecutiveUserId == CrmAccess.UserId(h) ||
             CrmAccess.Scope(h, "crm.account.read-all")), ct);

    private static bool CanRead(HttpContext h) =>
        CrmDataEndpoints.HasRead(h, "account") &&
        CrmAccess.Scope(h, "crm.analysis.read");

    private static async Task<IResult> List(Guid id, CrmDbContext db, HttpContext h,
        int? page, int? pageSize, CancellationToken ct)
    {
        if (!CanRead(h)) return CrmAccess.Denied();
        if (!await CanSeeAccount(db, h, id, ct)) return Results.NotFound();
        var actor = CrmAccess.UserId(h);
        var canReadMeetings = CrmDataEndpoints.HasRead(h, "activity");
        var canReadQuotes = CrmAccess.Scope(h, "crm.quote.read") &&
            CrmDataEndpoints.HasRead(h, "opportunity");
        var query = db.AccountRouteEvidence.AsNoTracking().Where(x => x.AccountId == id);
        if (!canReadMeetings) query = query.Where(x => x.SourceKind != "MEETING");
        else if (!CrmAccess.Scope(h, "crm.activity.read-all"))
        {
            var visibleActivityIds = db.Activities.AsNoTracking()
                .Where(x => x.AccountId == id && x.OwnerUserId == actor && x.ArchivedAt == null)
                .Select(x => x.Id);
            query = query.Where(x => x.SourceKind != "MEETING" ||
                visibleActivityIds.Contains(x.SourceId));
        }
        if (!canReadQuotes) query = query.Where(x => x.SourceKind != "QUOTE");
        else if (!CrmAccess.Scope(h, "crm.opportunity.read-all"))
        {
            var visibleQuoteIds =
                from q in db.OpportunityQuotes.AsNoTracking()
                join o in db.Opportunities.AsNoTracking() on q.OpportunityId equals o.Id
                where o.AccountId == id && o.ExecutiveUserId == actor && o.ArchivedAt == null
                select q.PricingQuoteId;
            query = query.Where(x => x.SourceKind != "QUOTE" ||
                visibleQuoteIds.Contains(x.SourceId));
        }
        var number = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 20, 1, 100);
        var total = await query.CountAsync(ct);
        var popularRoutes = await query.GroupBy(x => new { x.PolCode, x.PoeCode, x.PodCode })
            .Select(g => new {
                g.Key.PolCode, g.Key.PoeCode, g.Key.PodCode,
                Occurrences = g.Count(), LastSeenAt = g.Max(x => x.ObservedAt),
                MeetingCount = g.Count(x => x.SourceKind == "MEETING"),
                QuoteCount = g.Count(x => x.SourceKind == "QUOTE")
            }).OrderByDescending(x => x.Occurrences).ThenByDescending(x => x.LastSeenAt)
            .Take(10).ToListAsync(ct);
        var records = await query.OrderByDescending(x => x.ObservedAt)
            .ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip((number - 1) * size).Take(size)
            .Select(x => new {
                x.Id, x.AccountId, x.SourceKind, x.SourceId, x.PolCode, x.PoeCode,
                x.PodCode, x.ServiceCode, x.Notes, x.ObservedAt, x.CreatedAt
            }).ToListAsync(ct);
        return Results.Ok(new { items = records, total, page = number, pageSize = size, popularRoutes });
    }

    private static async Task<IResult> Add(Guid id, AddAccountRouteEvidenceRequest r,
        CrmDbContext db, HttpContext h, IConfigReferenceService config, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h, "crm.analysis.manage") ||
            !CanRead(h)) return CrmAccess.Denied();
        if (!await CanSeeAccount(db, h, id, ct)) return Results.NotFound();
        if (r.SourceKind is not ("MEETING" or "QUOTE") ||
            r.SourceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(r.PolCode) ||
            string.IsNullOrWhiteSpace(r.PodCode) ||
            r.PolCode.Length > 80 || r.PodCode.Length > 80 ||
            r.PoeCode?.Length > 80 || r.ServiceCode?.Length > 80 ||
            r.Notes?.Length > 2000)
            return CrmDataEndpoints.Bad("Invalid route or provenance");
        foreach (var (slug, value) in new[] {
            ("pol", r.PolCode), ("poe", r.PoeCode), ("pod", r.PodCode),
            ("pricing-services", r.ServiceCode)
        })
            if (!string.IsNullOrWhiteSpace(value) &&
                !await config.IsActiveCodeAsync(slug, value.Trim(), ct))
                return CrmDataEndpoints.Bad("Route port or service does not exist in Config");

        DateTime observedAt;
        if (r.SourceKind == "MEETING")
        {
            if (!CrmDataEndpoints.HasRead(h, "activity")) return CrmAccess.Denied();
            var activity = await db.Activities.AsNoTracking()
                .Where(x => x.Id == r.SourceId && x.AccountId == id && x.ArchivedAt == null &&
                    (x.Type == "VISIT" || x.Type == "MEETING") &&
                    x.Status == "Completed" &&
                    (x.OwnerUserId == CrmAccess.UserId(h) ||
                     CrmAccess.Scope(h, "crm.activity.read-all")))
                .Select(x => new { x.OccurredAt }).SingleOrDefaultAsync(ct);
            if (activity is null) return CrmDataEndpoints.Bad("Select a completed client meeting");
            observedAt = activity.OccurredAt ?? DateTime.UtcNow;
        }
        else
        {
            if (!CrmAccess.Scope(h, "crm.quote.read") ||
                !CrmDataEndpoints.HasRead(h, "opportunity"))
                return CrmAccess.Denied();
            var quote = await (
                from q in db.OpportunityQuotes.AsNoTracking()
                join o in db.Opportunities.AsNoTracking() on q.OpportunityId equals o.Id
                where q.PricingQuoteId == r.SourceId && o.AccountId == id &&
                    o.ArchivedAt == null && (o.ExecutiveUserId == CrmAccess.UserId(h) ||
                    CrmAccess.Scope(h, "crm.opportunity.read-all"))
                select new { q.LinkedAt }
            ).SingleOrDefaultAsync(ct);
            if (quote is null) return CrmDataEndpoints.Bad("Quote must be linked to this account");
            observedAt = quote.LinkedAt;
        }

        var pol = r.PolCode.Trim();
        var pod = r.PodCode.Trim();
        var poe = string.IsNullOrWhiteSpace(r.PoeCode) ? null : r.PoeCode.Trim();
        var existing = await db.AccountRouteEvidence.AsNoTracking()
            .FirstOrDefaultAsync(x => x.AccountId == id && x.SourceKind == r.SourceKind &&
                x.SourceId == r.SourceId && x.PolCode == pol && x.PodCode == pod &&
                x.PoeCode == poe, ct);
        if (existing is not null) return Results.Ok(existing);
        var record = new AccountRouteEvidence {
            AccountId = id, SourceKind = r.SourceKind, SourceId = r.SourceId,
            PolCode = pol, PoeCode = poe, PodCode = pod,
            ServiceCode = string.IsNullOrWhiteSpace(r.ServiceCode) ? null : r.ServiceCode.Trim(),
            Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim(),
            ObservedAt = observedAt, CreatedByUserId = CrmAccess.UserId(h)
        };
        db.AccountRouteEvidence.Add(record);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) {
            return CrmDataEndpoints.Conflict("Route was already captured or source changed");
        }
        return Results.Created("/api/crm/accounts/" + id + "/route-history/" + record.Id, record);
    }
}
