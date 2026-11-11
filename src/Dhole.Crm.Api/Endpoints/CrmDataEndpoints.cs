using System.Text.RegularExpressions;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.Realtime;
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
        group.MapAccountSpecificProcedures();
        group.MapAccountCommercialBenefits();
        group.MapLeads();
        group.MapPricingClientOptions();
        group.MapContacts();
        group.MapGeoLocations();
        group.MapCommercialPipeline();
        group.MapCommercialOpportunities();
        group.MapCommercialActivities();
        group.MapCrmDocuments();
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
    internal static string CategoryOnCreate(HttpContext http, string type, string requestedCode) =>
        AccountCategoryPolicy.ResolveOnCreate(type, requestedCode,
            CrmAccess.Scope(http, AccountCategoryManageScope));
    internal static bool CategoryValid(string type, string code) =>
        AccountCategoryPolicy.Valid(type, code);
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

    // F6.01: account lifecycle mutations share one transactional durable producer.
    // With batch flags OFF, preserve the existing REST-only save path.
    internal static async Task<IResult> CommitAccount(
        CrmDbContext db, Dhole.Crm.Domain.Account account,
        HttpContext http, IConfiguration flags, string eventType,
        CancellationToken ct)
    {
        if (!CrmAccountRealtimeCommit.IsEnabled(flags))
            return await Commit(db, ct);
        try
        {
            await CrmAccountRealtimeCommit.PersistUpdatedAccountAsync(
                db, account, http.TraceIdentifier, ct, eventType);
            return Results.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict("Record modified concurrently");
        }
        catch (DbUpdateException)
        {
            return Conflict("Conflicting or invalid CRM record");
        }
        catch (Npgsql.NpgsqlException)
        {
            // A missing Outbox migration must not silently lose a committed event.
            return Results.Problem(statusCode: 503,
                title: "CRM realtime persistence unavailable");
        }
    }

    internal static async Task<IResult> Commit(CrmDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); return Results.Ok(); }
        catch (DbUpdateConcurrencyException) { return Conflict("Record modified concurrently"); }
        catch (DbUpdateException) { return Conflict("Conflicting or invalid CRM record"); }
    }
}
