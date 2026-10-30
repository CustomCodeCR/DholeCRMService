using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

public sealed record UpsertAccountBusinessProfileRequest(
    long? ExpectedVersion, string? Phone, string? Email, string? Website,
    string? Sector, string? BusinessActivity, string? Products, string? CompanyHistory,
    string? BillingEmail, string? MarketingEmail,
    string? CommercialObservations, string? OperationalInstructions,
    string? ActiveServices,
    string? PresenceCountries,
    string? CommercialStrategy,
    string? ProjectsOfInterest,
    string? PricingObservations,
    string? TariffMonitoringNotes,
    string? TariffSendingNotes,
    string? CustomsInstructions,
    string? CargoCollectionInstructions,
    string? DocumentReviewInstructions,
    string? AssignedOperations,
    string? CompetitorObservations,
    string? TradeAgreementNotes,
    string? WebCustomerReference);

internal static class AccountBusinessProfileEndpoints
{
    public static RouteGroupBuilder MapAccountBusinessProfile(this RouteGroupBuilder g)
    {
        g.MapGet("/accounts/{id:guid}/business-profile", Get);
        g.MapPut("/accounts/{id:guid}/business-profile", Upsert);
        return g;
    }

    private static async Task<bool> CanAccess(CrmDbContext db, HttpContext h,
        Guid accountId, CancellationToken ct) =>
        await CrmAccess.CanSeeAccount(db,h,accountId,ct);

    private static async Task<IResult> Get(Guid id, CrmDbContext db,
        HttpContext h, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(h, "account")) return CrmAccess.Denied();
        if (!await CanAccess(db, h, id, ct)) return Results.NotFound();
        var profile = await db.AccountBusinessProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AccountId == id, ct);
        return Results.Ok(profile);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool Valid(string? value, int max) => value is null || value.Length <= max;

    private static async Task<IResult> Upsert(Guid id, UpsertAccountBusinessProfileRequest r,
        CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h, "crm.account.update")) return CrmAccess.Denied();
        if (!await CanAccess(db, h, id, ct)) return Results.NotFound();
        if (!Valid(r.Phone, 60) || !Valid(r.Email, 254) ||
            !Valid(r.Website, 500) || !Valid(r.Sector, 350) ||
            !Valid(r.BusinessActivity, 2000) || !Valid(r.Products, 2000) ||
            !Valid(r.CompanyHistory, 4000) || !Valid(r.BillingEmail, 254) ||
            !Valid(r.MarketingEmail, 254) || !Valid(r.CommercialObservations, 4000) ||
            !Valid(r.OperationalInstructions, 4000) ||
            !Valid(r.ActiveServices, 2000) ||
            !Valid(r.PresenceCountries, 2000) ||
            !Valid(r.CommercialStrategy, 4000) ||
            !Valid(r.ProjectsOfInterest, 4000) ||
            !Valid(r.PricingObservations, 4000) ||
            !Valid(r.TariffMonitoringNotes, 4000) ||
            !Valid(r.TariffSendingNotes, 4000) ||
            !Valid(r.CustomsInstructions, 4000) ||
            !Valid(r.CargoCollectionInstructions, 4000) ||
            !Valid(r.DocumentReviewInstructions, 4000) ||
            !Valid(r.AssignedOperations, 500) ||
            !Valid(r.CompetitorObservations, 4000) ||
            !Valid(r.TradeAgreementNotes, 2000) ||
            !Valid(r.WebCustomerReference, 200) ||
            !CrmDataEndpoints.IsEmailValid(r.Email) ||
            !CrmDataEndpoints.IsEmailValid(r.BillingEmail) ||
            !CrmDataEndpoints.IsEmailValid(r.MarketingEmail))
            return CrmDataEndpoints.Bad("Invalid company contact or profile fields");

        if (!string.IsNullOrWhiteSpace(r.Website) &&
            (!Uri.TryCreate(r.Website, UriKind.Absolute, out var site) ||
             site.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(site.UserInfo)))
            return CrmDataEndpoints.Bad("Company website must be a valid HTTP(S) URL");

        var profile = await db.AccountBusinessProfiles
            .SingleOrDefaultAsync(x => x.AccountId == id, ct);
        if (profile is null)
        {
            if (r.ExpectedVersion is not null)
                return CrmDataEndpoints.Conflict("Company profile does not yet exist");
            profile = new AccountBusinessProfile {
                AccountId = id, CreatedByUserId = CrmAccess.UserId(h)
            };
            db.AccountBusinessProfiles.Add(profile);
        }
        else
        {
            if (r.ExpectedVersion != profile.RowVersion)
                return CrmDataEndpoints.Conflict("Company profile has changed; reload first");
            profile.RowVersion++;
            profile.UpdatedAt = DateTime.UtcNow;
            profile.UpdatedByUserId = CrmAccess.UserId(h);
        }
        profile.Phone = Normalize(r.Phone);
        profile.Email = Normalize(r.Email);
        profile.Website = Normalize(r.Website);
        profile.Sector = Normalize(r.Sector);
        profile.BusinessActivity = Normalize(r.BusinessActivity);
        profile.Products = Normalize(r.Products);
        profile.CompanyHistory = Normalize(r.CompanyHistory);
        profile.BillingEmail = Normalize(r.BillingEmail);
        profile.MarketingEmail = Normalize(r.MarketingEmail);
        profile.CommercialObservations = Normalize(r.CommercialObservations);
        profile.OperationalInstructions = Normalize(r.OperationalInstructions);
        profile.ActiveServices = Normalize(r.ActiveServices);
        profile.PresenceCountries = Normalize(r.PresenceCountries);
        profile.CommercialStrategy = Normalize(r.CommercialStrategy);
        profile.ProjectsOfInterest = Normalize(r.ProjectsOfInterest);
        profile.PricingObservations = Normalize(r.PricingObservations);
        profile.TariffMonitoringNotes = Normalize(r.TariffMonitoringNotes);
        profile.TariffSendingNotes = Normalize(r.TariffSendingNotes);
        profile.CustomsInstructions = Normalize(r.CustomsInstructions);
        profile.CargoCollectionInstructions = Normalize(r.CargoCollectionInstructions);
        profile.DocumentReviewInstructions = Normalize(r.DocumentReviewInstructions);
        profile.AssignedOperations = Normalize(r.AssignedOperations);
        profile.CompetitorObservations = Normalize(r.CompetitorObservations);
        profile.TradeAgreementNotes = Normalize(r.TradeAgreementNotes);
        profile.WebCustomerReference = Normalize(r.WebCustomerReference);
        try
        {
            await db.SaveChangesAsync(ct);
            return Results.Ok(profile);
        }
        catch (DbUpdateException) { return CrmDataEndpoints.Conflict("Company profile already updated"); }
    }
}
