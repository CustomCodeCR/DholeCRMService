using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using System.Net;
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
        g.MapGet("/accounts/{id:guid}/quote-route-snapshots", QuoteRoutes);
        g.MapGet("/accounts/{id:guid}/pricing-quotations", AccountQuotes);
        g.MapGet("/accounts/{id:guid}/pricing-quotation-candidates", LegacyCandidates);
        g.MapPost("/accounts/{id:guid}/pricing-quotations/reconcile", ReconcileLegacyQuotes);
        g.MapGet("/leads/{id:guid}/pricing-quotations", LeadQuotes);
        return g;
    }

    private static async Task<bool> CanSeeAccount(CrmDbContext db, HttpContext h,
        Guid id, CancellationToken ct) =>
        await CrmAccess.CanSeeAccount(db,h,id,ct);

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

    // Only account-scoped quotes verified by Pricing using the caller's JWT.
    // The association is persisted by account ID; never infer from client name.
    private static async Task<IResult> AccountQuotes(Guid id, CrmDbContext db,
        HttpContext h, IPricingCommercialClient pricing, int? page, int? pageSize,
        CancellationToken ct)
    {
        // CRM account visibility authorizes the account-scoped view. The remote
        // Pricing API independently requires pricing.rate.view on the caller token.
        // crm.quote.read is specific to commercial opportunities, not mandatory
        // for viewing an existing customer's own historical rates.
        if (!CrmDataEndpoints.HasRead(h, "account")) return CrmAccess.Denied();
        if (!await CanSeeAccount(db, h, id, ct)) return Results.NotFound();

        var authorization = h.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Results.Unauthorized();
        var bearer = authorization["Bearer ".Length..].Trim();
        var linkedLeadIds = await db.Leads.AsNoTracking()
            .Where(x => x.ConvertedAccountId == id && x.ConvertedAt != null)
            .Select(x => x.Id).Distinct().Take(100).ToArrayAsync(ct);
        var result = await pricing.GetAccountQuotes(id, linkedLeadIds, bearer,
            Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 100, 1, 100), ct);
        if (result.Success) return Results.Ok(result.Value);
        return result.Status switch
        {
            HttpStatusCode.Unauthorized => Results.Unauthorized(),
            HttpStatusCode.Forbidden => Results.Problem(statusCode: 403,
                title: "Pricing requiere permiso pricing.rate.view para consultar cotizaciones"),
            HttpStatusCode.NotFound => Results.NotFound(),
            _ => Results.Problem(statusCode: 503,
                title: "No se pudo consultar el historial de Pricing")
        };
    }

    public sealed record ReconcileLegacyQuotesRequest(Guid[] QuoteIds);

    private static async Task<(string? LegalName, IResult? Error)> GetVerifiedLegacyAccountName(
        Guid id, CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(h, "account"))
            return (null, CrmAccess.Denied());
        var account = await CrmAccess.VisibleAccounts(db, h).AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.LegalName }).SingleOrDefaultAsync(ct);
        if (account is null) return (null, Results.NotFound());
        var legalName = account.LegalName.Trim();
        if (legalName.Length is < 3 or > 220)
            return (null, Results.Problem(statusCode: 409, title: "La razón social no es válida."));
        // Refuse to reconcile a shared corporate name automatically to different CRM accounts.
        var duplicates = await db.Accounts.AsNoTracking()
            .CountAsync(x => x.ArchivedAt == null &&
                x.LegalName.Trim().ToUpper() == legalName.ToUpperInvariant(), ct);
        if (duplicates != 1)
            return (null, Results.Problem(statusCode: 409,
                title: "Hay varias cuentas con esta razón social; primero se deben resolver los duplicados."));
        return (legalName, null);
    }

    private static string? Bearer(HttpContext h)
    {
        var token = h.Request.Headers.Authorization.ToString();
        return token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? token["Bearer ".Length..].Trim() : null;
    }

    private static IResult PricingFailure(HttpStatusCode status, string action) => status switch
    {
        HttpStatusCode.Unauthorized => Results.Unauthorized(),
        HttpStatusCode.Forbidden => Results.Problem(statusCode: 403,
            title: "Pricing requiere permiso para " + action),
        HttpStatusCode.Conflict => Results.Problem(statusCode: 409,
            title: "La conciliación de cotizaciones fue rechazada por Pricing."),
        _ => Results.Problem(statusCode: 503,
            title: "No fue posible " + action + " en Pricing.")
    };

    // Candidates are suggestions only. No relationship is written without explicit
    // confirmation by an account editor and an independent Pricing update grant.
    private static async Task<IResult> LegacyCandidates(Guid id, CrmDbContext db,
        HttpContext h, IPricingCommercialClient pricing, int? page, int? pageSize,
        CancellationToken ct)
    {
        if (!CrmAccess.Scope(h, "crm.account.update")) return CrmAccess.Denied();
        var (name, error) = await GetVerifiedLegacyAccountName(id, db, h, ct);
        if (error is not null) return error;
        var bearer = Bearer(h);
        if (string.IsNullOrWhiteSpace(bearer)) return Results.Unauthorized();
        var result = await pricing.GetLegacyCandidates(name!, bearer,
            Math.Clamp(page ?? 1, 1, 100000), Math.Clamp(pageSize ?? 30, 1, 100), ct);
        return result.Success ? Results.Ok(result.Value) :
            PricingFailure(result.Status, "buscar cotizaciones anteriores");
    }

    private static async Task<IResult> ReconcileLegacyQuotes(Guid id,
        ReconcileLegacyQuotesRequest request, CrmDbContext db,
        HttpContext h, IPricingCommercialClient pricing, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h, "crm.account.update")) return CrmAccess.Denied();
        if (request.QuoteIds is not { Length: > 0 and <= 100 } ||
            request.QuoteIds.Any(x => x == Guid.Empty))
            return Results.BadRequest(new { message = "Seleccione de 1 a 100 cotizaciones." });
        var (name, error) = await GetVerifiedLegacyAccountName(id, db, h, ct);
        if (error is not null) return error;
        var bearer = Bearer(h);
        if (string.IsNullOrWhiteSpace(bearer)) return Results.Unauthorized();
        var result = await pricing.ReconcileLegacyQuotes(id, name!,
            request.QuoteIds.Distinct().ToArray(), bearer, ct);
        return result.Success ? Results.Ok(result.Value) :
            PricingFailure(result.Status, "vincular las cotizaciones históricas");
    }

    private static async Task<IResult> LeadQuotes(Guid id, CrmDbContext db,
        HttpContext h, IPricingCommercialClient pricing, int? page, int? pageSize,
        CancellationToken ct)
    {
        // Lead readers may consult this lead's rates without the optional
        // opportunity quote scope; Pricing still authorizes the caller.
        if (!CrmDataEndpoints.HasRead(h, "lead")) return CrmAccess.Denied();
        var lead = await db.Leads.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        if (lead is null) return Results.NotFound();
        if (!CrmAccess.LeadAccess(h, lead.ExecutiveUserId)) return CrmAccess.Denied();

        var authorization = h.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Results.Unauthorized();
        var bearer = authorization["Bearer ".Length..].Trim();
        var response = await pricing.GetLeadQuotes(id, bearer,
            Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 100, 1, 100), ct);
        if (response.Success) return Results.Ok(response.Value);
        return response.Status switch
        {
            HttpStatusCode.Unauthorized => Results.Unauthorized(),
            HttpStatusCode.Forbidden => Results.Problem(statusCode: 403,
                title: "Pricing requiere permiso pricing.rate.view para consultar cotizaciones"),
            HttpStatusCode.NotFound => Results.NotFound(),
            _ => Results.Problem(statusCode: 503,
                title: "No se pudo consultar las cotizaciones del prospecto")
        };
    }

    private static async Task<IResult> QuoteRoutes(Guid id, CrmDbContext db,
        HttpContext h, IPricingCommercialClient pricing, int? page, int? pageSize,
        CancellationToken ct)
    {
        // The account viewer can request the account's historical quote routes.
        // Pricing independently authorizes every quote with the caller's bearer token.
        if (!CrmDataEndpoints.HasRead(h, "account")) return CrmAccess.Denied();
        if (!await CanSeeAccount(db, h, id, ct)) return Results.NotFound();
        var visibleOpportunities = db.Opportunities.AsNoTracking()
            .Where(o => o.AccountId == id && o.ArchivedAt == null);
        var linked = from q in db.OpportunityQuotes.AsNoTracking()
                     join op in visibleOpportunities on q.OpportunityId equals op.Id
                     select q;
        var pageNumber = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 8, 1, 10);
        var total = await linked.CountAsync(ct);
        var records = await linked.OrderByDescending(x => x.LinkedAt).ThenBy(x => x.Id)
            .Skip((pageNumber - 1) * size).Take(size)
            .Select(x => new { x.PricingQuoteId, x.QuoteNumberSnapshot,
                x.ShipmentModeSnapshot, x.LinkedAt }).ToListAsync(ct);
        var authorization = h.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Results.Unauthorized();
        var bearer = authorization["Bearer ".Length..].Trim();
        // An independent Pricing authorization check occurs for every linked quote.
        // Small bounded pages avoid uncontrolled fan-out to Pricing.
        var tasks = records.Select(async row =>
        {
            var response = await pricing.GetVisibleQuoteRoute(row.PricingQuoteId, bearer, ct);
            var route = response.Success ? response.Value : null;
            return new {
                pricingQuoteId = route is null ? (Guid?)null : row.PricingQuoteId,
                quoteNumber = route is null ? null : row.QuoteNumberSnapshot,
                shipmentMode = route is null ? null : row.ShipmentModeSnapshot,
                linkedAt = route is null ? (DateTime?)null : row.LinkedAt,
                polName = route?.PolName,
                poeName = route?.PoeName,
                podName = route?.PodName,
                revisionNumber = route?.RevisionNumber,
                revisionPending = route?.RevisionPending,
                available = route is not null &&
                    (!string.IsNullOrWhiteSpace(route.PolName) ||
                     !string.IsNullOrWhiteSpace(route.PodName))
            };
        }).ToArray();
        var items = await Task.WhenAll(tasks);
        return Results.Ok(new { items, page = pageNumber, pageSize = size, total,
            note = "Current authorized Pricing view; published revision when draft is pending." });
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
                orderby q.LinkedAt descending
                select new { q.LinkedAt }
            ).FirstOrDefaultAsync(ct);
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
