using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

/// <summary>
/// Account-centered CRM read model. No replicated Pricing rows or cross-service FKs:
/// only the quotes linked by CRM, with their last confirmed commercial snapshots.
/// All sections apply the same record ownership and effective-scope rules as their
/// underlying CRM resources. Avoid leaking other sellers' information.
/// </summary>
internal static class AccountCommercialOverviewEndpoints
{
    public static RouteGroupBuilder MapAccountCommercialOverview(this RouteGroupBuilder group)
    {
        group.MapGet("/accounts/{id:guid}/commercial-overview", Get);
        group.MapGet("/accounts/{id:guid}/quote-dashboard", GetQuoteDashboard);
        return group;
    }

    /// <summary>
    /// Aggregates every distinct, explicitly linked Pricing quote for this account.
    /// Counts are not limited by the commercial-overview preview's 100-row cap.
    /// Never attribute a quote to an account by comparing customer names.
    /// Statuses reflect the most recent synchronized Pricing snapshot.
    /// </summary>
    private static async Task<IResult> GetQuoteDashboard(Guid id, CrmDbContext db,
        HttpContext http, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "account") ||
            !CrmDataEndpoints.HasRead(http, "opportunity") ||
            !CrmAccess.Scope(http, "crm.quote.read"))
            return CrmAccess.Denied();

        var account = await CrmAccess.VisibleAccounts(db, http).AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.IsCastroFallasExecutive })
            .SingleOrDefaultAsync(ct);
        if (account is null) return Results.NotFound();

        var actor = CrmAccess.UserId(http);
        var allOpportunities = CrmAccess.Scope(http, "crm.opportunity.read-all") ||
            account.IsCastroFallasExecutive;
        var visible = db.Opportunities.AsNoTracking()
            .Where(x => x.AccountId == id && x.ArchivedAt == null &&
                (allOpportunities || x.ExecutiveUserId == actor));

        var linked = await (
            from quote in db.OpportunityQuotes.AsNoTracking()
            join opportunity in visible on quote.OpportunityId equals opportunity.Id
            select new {
                quote.PricingQuoteId, quote.CommercialStatusSnapshot,
                quote.LinkedAt, quote.LastSyncAt, quote.PendingRefreshAt
            }).ToListAsync(ct);

        // A Pricing quote linked to multiple opportunities of this same client
        // must never inflate the count.
        var quotes = linked
            .GroupBy(x => x.PricingQuoteId)
            .Select(g => g.OrderByDescending(x => x.LastSyncAt).First())
            .ToArray();
        static bool Is(string? status, string expected) =>
            string.Equals(status, expected, StringComparison.OrdinalIgnoreCase);
        var sent = quotes.Count(q => Is(q.CommercialStatusSnapshot, "Sent") ||
            Is(q.CommercialStatusSnapshot, "RequestedByClient") ||
            Is(q.CommercialStatusSnapshot, "Expired"));
        var approved = quotes.Count(q => Is(q.CommercialStatusSnapshot, "AcceptedByClient"));
        var rejected = quotes.Count(q => Is(q.CommercialStatusSnapshot, "RejectedByClient"));
        var cancelled = quotes.Count(q => Is(q.CommercialStatusSnapshot, "Cancelled"));
        var other = quotes.Length - sent - approved - rejected - cancelled;

        return Results.Ok(new {
            total = quotes.Length, sent, approved, rejected, cancelled, other,
            pendingSync = quotes.Count(q => q.PendingRefreshAt.HasValue &&
                q.PendingRefreshAt.Value > q.LastSyncAt),
            lastSyncAt = quotes.Length == 0 ? (DateTime?)null : quotes.Max(q => q.LastSyncAt),
            linkedOnly = true
        });
    }

    private static async Task<IResult> Get(Guid id, CrmDbContext db, HttpContext http,
        CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "account"))
            return CrmAccess.Denied();
        var account = await CrmAccess.VisibleAccounts(db, http).AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.Id, x.IsCastroFallasExecutive })
            .SingleOrDefaultAsync(ct);
        if (account is null) return Results.NotFound();

        var actor = CrmAccess.UserId(http);
        var allActivities = CrmAccess.Scope(http, "crm.activity.read-all") || account.IsCastroFallasExecutive;
        var allOpportunities = CrmAccess.Scope(http, "crm.opportunity.read-all") || account.IsCastroFallasExecutive;
        var allAccounts = CrmAccess.Scope(http, "crm.account.read-all");
        var allAnalyses = allAccounts || allOpportunities || account.IsCastroFallasExecutive;

        // Capped, separate reads: the account page should not load unlimited PII or
        // financial data. Each collection intentionally remains an independent result.
        object[] activities = [];
        if (CrmDataEndpoints.HasRead(http, "activity"))
        {
            activities = await db.Activities.AsNoTracking()
                .Where(x => x.AccountId == id && x.ArchivedAt == null &&
                    (allActivities || x.OwnerUserId == actor))
                .OrderByDescending(x => x.OccurredAt ?? x.ScheduledStartAt ?? x.CreatedAt)
                .Take(50)
                .Select(x => new {
                    x.Id, x.Type, x.Subject, x.Status, x.Outcome, x.OwnerUserId,
                    x.OccurredAt, x.ScheduledStartAt, x.LocationText
                }).ToArrayAsync(ct);
        }

        object[] followups = [];
        if (CrmAccess.Scope(http, "crm.followup.manage") ||
            CrmAccess.Scope(http, "crm.task.manage"))
        {
            followups = await db.Followups.AsNoTracking()
                .Where(x => x.AccountId == id &&
                    (x.AssignedUserId == actor || allAccounts || account.IsCastroFallasExecutive))
                .OrderBy(x => x.DueAt).Take(50)
                .Select(x => new { x.Id, x.Channel, x.DueAt, x.Status,
                    x.Outcome, x.AssignedUserId }).ToArrayAsync(ct);
        }

        object[] routes = [];
        object[] analyses = [];
        object[] analysisRoutes = [];
        if (CrmAccess.Scope(http, "crm.analysis.read"))
        {
            routes = await db.CustomerTradeProfiles.AsNoTracking()
                .Where(x => x.AccountId == id)
                .OrderByDescending(x => x.IsActive).ThenBy(x => x.ServiceCode)
                .Take(100)
                .Select(x => new { x.Id, x.ServiceCode, x.PreferredOriginCode,
                    x.PreferredDestinationCode, x.Frequency, x.ExpectedVolume,
                    x.VolumeUnit, x.IsActive }).ToArrayAsync(ct);
            analyses = await db.CommercialAnalyses.AsNoTracking()
                .Where(x => x.AccountId == id &&
                    (allAnalyses || x.OwnerUserId == actor))
                .OrderByDescending(x => x.CreatedAt).Take(30)
                .Select(x => new { x.Id, x.MarketCode, x.CustomsNationalizationRequired,
                    x.EstimatedMonthlyVolume, x.VolumeUnit, x.CreatedAt }).ToArrayAsync(ct);
            var visibleAnalysisIds = db.CommercialAnalyses.AsNoTracking()
                .Where(x => x.AccountId == id &&
                    (allAnalyses || x.OwnerUserId == actor))
                .Select(x => x.Id);
            analysisRoutes = await db.CommercialAnalysisRoutes.AsNoTracking()
                .Where(x => visibleAnalysisIds.Contains(x.AnalysisId))
                .OrderByDescending(x => x.CreatedAt).Take(80)
                .Select(x => new { x.Id, x.PolCode, x.PoeCode, x.PodCode,
                    x.FrequencyPerMonth, x.Notes, x.AnalysisId }).ToArrayAsync(ct);
        }

        object[] opportunities = [];
        object[] quotes = [];
        if (CrmDataEndpoints.HasRead(http, "opportunity"))
        {
            var visible = db.Opportunities.AsNoTracking()
                .Where(x => x.AccountId == id && x.ArchivedAt == null &&
                    (allOpportunities || x.ExecutiveUserId == actor));
            opportunities = await visible
                .OrderByDescending(x => x.CreatedAt).Take(50)
                .Select(x => new { x.Id, x.Title, x.Status, x.AmountEstimated,
                    x.CurrencyCode, x.ExpectedCloseAt }).ToArrayAsync(ct);

            if (CrmAccess.Scope(http, "crm.quote.read"))
            {
                // The filter stays inside SQL: never infer quote ownership from a
                // customer name or import historical quotes from an unrelated account.
                quotes = await (
                    from quote in db.OpportunityQuotes.AsNoTracking()
                    join opportunity in visible on quote.OpportunityId equals opportunity.Id
                    orderby quote.LinkedAt descending
                    select new { quote.PricingQuoteId, quote.QuoteNumberSnapshot,
                        quote.ShipmentModeSnapshot, quote.CommercialStatusSnapshot,
                        quote.OpportunityId, quote.LinkedAt, quote.LastSyncAt }
                ).Take(100).ToArrayAsync(ct);
            }
        }

        return Results.Ok(new {
            activities, followups, routes, analysisRoutes, analyses, opportunities, quotes,
            quotesAreLinkedOnly = true
        });
    }
}
