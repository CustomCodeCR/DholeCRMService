using System.Text.RegularExpressions;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

public static class CrmDataEndpoints
{
    public static IEndpointRouteBuilder MapCrmDataEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/crm").WithTags("CRM Data").RequireAuthorization()
            .AddEndpointFilter(CrmAccess.Verify);
        group.MapHaciendaTaxpayers();
        group.MapAccounts();
        group.MapCastroFallasGlobalAccess();
        group.MapAccountCommercialOverview();
        group.MapAccountRouteEvidence();
        group.MapAccountBusinessProfile();
        group.MapAccountCommercialBenefits();
        group.MapLeads();
        group.MapPricingClientOptions();
        group.MapContacts();
        group.MapGeoLocations();
        group.MapCommercialPipeline();
        group.MapCommercialOpportunities();
        group.MapCommercialActivities();
        group.MapCalendarLabels();
        group.MapCommercialAnalyses();
        group.MapCommercialPricing();
        group.MapFollowups();
        group.MapCredit();
        group.MapCreditContracts();
        return app;
    }

    internal static string? NormalizeTax(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null :
        Regex.Replace(value.Trim().ToUpperInvariant(), @"[\s.\-]", "");
    internal const string AccountCategoryManageScope = "crm.account.category.manage";
    // A safe initial classification is assigned by CRM, never by an unprivileged client.
    // The category remains one of the existing supported codes, preserving legacy integrations.
    internal static string DefaultAccountCategory(string type) => type switch
    {
        "CUSTOMER" => "D",
        "NVOCC" => "B",
        "LITTLE" => "A",
        _ => "D"
    };
    internal static string CategoryOnCreate(HttpContext http, string type, string requestedCode) =>
        CrmAccess.Scope(http, AccountCategoryManageScope)
            ? requestedCode
            : DefaultAccountCategory(type);
    internal static bool CategoryValid(string type, string code) =>
        code is { Length: 1 } &&
        ((type == "CUSTOMER" && "ABCD".Contains(code, StringComparison.Ordinal)) ||
         (type == "NVOCC" && "AB".Contains(code, StringComparison.Ordinal)) ||
         (type == "LITTLE" && code == "A"));
    internal static bool IsEmailValid(string? address) =>
        string.IsNullOrWhiteSpace(address) ||
        (System.Net.Mail.MailAddress.TryCreate(address, out var parsed) &&
         parsed.Address.Equals(address.Trim(), StringComparison.OrdinalIgnoreCase));
    internal static bool HasRead(HttpContext http, string kind) =>
        CrmAccess.Scope(http, "crm." + kind + ".read") ||
        CrmAccess.Scope(http, "crm." + kind + ".read-all");
    internal static async Task<bool> ValidCountry(IConfigReferenceService config, Guid? id, CancellationToken ct) =>
        id is null || await config.IsActiveCountryAsync(id.Value, ct);
    internal static IResult Conflict(string title) => Results.Problem(statusCode: 409, title: title);
    internal static IResult Bad(string title) => Results.Problem(statusCode: 400, title: title);
    internal static IResult Unavailable() => Results.Problem(statusCode: 503, title: "Reference service unavailable");

    internal static async Task<IResult> Commit(CrmDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); return Results.Ok(); }
        catch (DbUpdateConcurrencyException) { return Conflict("Record modified concurrently"); }
        catch (DbUpdateException) { return Conflict("Conflicting or invalid CRM record"); }
    }
}
