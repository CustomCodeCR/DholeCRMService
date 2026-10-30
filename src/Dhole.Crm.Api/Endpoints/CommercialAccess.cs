using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;
internal static class CommercialAccess
{
    public static bool OpportunityCanRead(HttpContext h) => CrmDataEndpoints.HasRead(h, "opportunity");
    public static bool OpportunityOwns(HttpContext h, Opportunity o) =>
        o.ExecutiveUserId == CrmAccess.UserId(h) || CrmAccess.Scope(h, "crm.opportunity.read-all");
    public static async Task<bool> OpportunityOwnsAccount(CrmDbContext db, HttpContext h,
        Opportunity o, CancellationToken ct) =>
        OpportunityOwns(h,o) && (!o.AccountId.HasValue ||
            await CrmAccess.CanSeeAccount(db,h,o.AccountId.Value,ct));
    public static bool ActivityOwns(HttpContext h, Activity a) =>
        a.OwnerUserId == CrmAccess.UserId(h) || CrmAccess.Scope(h, "crm.activity.read-all");
    public static bool AnalysisOwns(HttpContext h, CommercialAnalysis a) =>
        a.OwnerUserId == CrmAccess.UserId(h) ||
        (a.OpportunityId is not null && CrmAccess.Scope(h,"crm.opportunity.read-all")) ||
        (a.AccountId is not null && CrmAccess.Scope(h,"crm.account.read-all")) ||
        (a.LeadId is not null && CrmAccess.Scope(h,"crm.lead.read-all"));

    public static async Task<bool> SubjectCanRead(CrmDbContext db, HttpContext h,
        Guid? accountId, Guid? leadId, Guid? opportunityId, CancellationToken ct)
    {
        if (accountId is null && leadId is null && opportunityId is null) return false;
        if (accountId is { } a &&
            !await CrmAccess.CanSeeAccount(db,h,a,ct)) return false;
        if (leadId is { } l &&
            !await db.Leads.AsNoTracking().AnyAsync(x => x.Id == l && x.ArchivedAt == null &&
                (x.ExecutiveUserId == CrmAccess.UserId(h) ||
                 CrmAccess.Scope(h, "crm.lead.read-all")),ct)) return false;
        if (opportunityId is { } o)
        {
            var opp = await db.Opportunities.AsNoTracking().FirstOrDefaultAsync(x => x.Id == o && x.ArchivedAt == null,ct);
            if (opp is null || !OpportunityOwns(h,opp) ||
                (accountId.HasValue && opp.AccountId != accountId) ||
                (leadId.HasValue && opp.LeadId != leadId)) return false;
        }
        return true;
    }
    public static async Task<bool> ContactBelongsTo(CrmDbContext db, Guid contactId,
        Guid? accountId, Guid? opportunityId, CancellationToken ct)
    {
        var actual = accountId;
        if (actual is null && opportunityId is { } opportunity)
            actual = await db.Opportunities.Where(x=>x.Id==opportunity).Select(x=>x.AccountId).FirstOrDefaultAsync(ct);
        return actual is { } a && await db.AccountContacts.AsNoTracking().AnyAsync(
            r => r.AccountId == a && r.ContactId == contactId && r.ActiveTo == null,ct);
    }
    public static Task<bool> ValidPartyContact(CrmDbContext db, Guid? contactId,
        Guid? accountId, Guid? opportunityId, CancellationToken ct) =>
        contactId is null ? Task.FromResult(true) :
            ContactBelongsTo(db,contactId.Value,accountId,opportunityId,ct);
    public static IResult Conflict(string reason) => CrmDataEndpoints.Conflict(reason);
    public static IResult Invalid(string reason) => CrmDataEndpoints.Bad(reason);
    public static Task<IResult> Save(CrmDbContext db, CancellationToken ct) =>
        CrmDataEndpoints.Commit(db,ct);
}
