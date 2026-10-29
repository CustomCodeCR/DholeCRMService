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
        return group;
    }

    private static async Task<IResult> Get(Guid id, CrmDbContext db, HttpContext http,
        CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "account"))
            return CrmAccess.Denied();
        var account = await db.Accounts.AsNoTracking()
            .Where(x => x.Id == id && x.ArchivedAt == null)
            .Select(x => new { x.Id, x.ExecutiveUserId })
            .SingleOrDefaultAsync(ct);
        if (account is null) return Results.NotFound();
        if (!CrmAccess.AccountAccess(http, account.ExecutiveUserId))
            return CrmAccess.Denied();

        var actor = CrmAccess.UserId(http);
        var allActivities = CrmAccess.Scope(http, "crm.activity.read-all");
        var allOpportunities = CrmAccess.Scope(http, "crm.opportunity.read-all");
        var allAccounts = CrmAccess.Scope(http, "crm.account.read-all");
        var allAnalyses = allAccounts || allOpportunities;

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
                    (x.AssignedUserId == actor || allAccounts))
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
