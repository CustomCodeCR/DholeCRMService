using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

/// <summary>
/// CRM-owned, read-only, internal acceptance projection. Both routes require
/// the internal service key plus a real active Auth identity with
/// crm.account.read-all (configured service reader, or verified accepting actor).
/// Name recovery never guesses: exactly one active account must match.
/// </summary>
internal static class OperationsAccountContractEndpoints
{
    public static IEndpointRouteBuilder MapOperationsAccountContract(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/internal/crm/operations/accounts/{accountId:guid}", async (
            Guid accountId, HttpContext http, IConfiguration configuration,
            IAuthReferenceService auth, CrmDbContext db, CancellationToken ct) =>
        {
            var denial = await VerifyReaderAsync(http, configuration, auth, ct);
            if (denial is not null) return denial;
            if (accountId == Guid.Empty) return Results.BadRequest();
            return await ProjectAccountAsync(db, auth, accountId, ct);
        });

        // Only the trusted Operations service can assert the case/account binding after its
        // own case ACL check. Auth still verifies the requesting user's current read scope.
        app.MapGet("/api/internal/crm/operations/accounts/{accountId:guid}/contacts", async (
            Guid accountId, HttpContext http, IConfiguration configuration,
            IAuthReferenceService auth, CrmDbContext db, CancellationToken ct, Guid? divisionId) =>
        {
            if (!CrmOperationsIntegrationAccessPolicy.HasServiceKey(configuration["Auth:Client:InternalServiceKey"],
                    http.Request.Headers["X-Dhole-Service-Key"].ToString())) return Results.Unauthorized();
            if (!Guid.TryParse(http.Request.Headers["X-Dhole-Operations-Actor"].ToString(), out var actor)
                || actor == Guid.Empty) return Results.Forbid();
            AuthUserReference? user;
            try { user = await auth.GetActiveAsync(actor, ct); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return Results.Problem(statusCode: 503, title: "CRM Auth verification unavailable");
            }
            if (user is null || !user.Active ||
                !user.Scopes.Contains("operations.case.read", StringComparer.OrdinalIgnoreCase)) return Results.Forbid();
            if (!await db.Accounts.AsNoTracking().AnyAsync(x => x.Id == accountId && x.ArchivedAt == null, ct))
                return Results.NotFound();
            return await ContactEndpoints.ProjectOperationsContactsAsync(accountId, db, ct, divisionId);
        });

        // Legacy Pricing quotes may contain a saved client name but no CRM
        // account GUID. Recover only when CRM confirms one exact active match.
        // Ambiguous/nonexistent names remain pending; no phantom accounts.
        app.MapGet("/api/internal/crm/operations/accounts/by-name", async (
            string? name, HttpContext http, IConfiguration configuration,
            IAuthReferenceService auth, CrmDbContext db, CancellationToken ct) =>
        {
            var denial = await VerifyReaderAsync(http, configuration, auth, ct);
            if (denial is not null) return denial;
            var normalized = name?.Trim();
            if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 200)
                return Results.BadRequest();

            var matches = await db.Accounts.AsNoTracking()
                .Where(a => a.ArchivedAt == null && a.Status == "Active"
                    && a.DisplayName.Trim().ToLower() == normalized.ToLower())
                .Select(a => a.Id)
                .Take(2)
                .ToArrayAsync(ct);
            if (matches.Length == 0) return Results.NotFound();
            if (matches.Length > 1)
                return Results.Conflict(new { code = "CRM_ACCOUNT_NAME_AMBIGUOUS" });
            return await ProjectAccountAsync(db, auth, matches[0], ct);
        });
        return app;
    }

    private static async Task<IResult?> VerifyReaderAsync(
        HttpContext http, IConfiguration configuration, IAuthReferenceService auth,
        CancellationToken ct)
    {
        var expectedKey = configuration["Auth:Client:InternalServiceKey"];
        var providedKey = http.Request.Headers["X-Dhole-Service-Key"].ToString();
        if (!CrmOperationsIntegrationAccessPolicy.HasServiceKey(expectedKey, providedKey))
            return Results.Unauthorized();

        // A configured dedicated service reader is preferred. When not
        // provisioned, use the actual Pricing acceptance actor only after
        // validating that identity and its CRM read-all scope in Auth.
        var configuredReader = configuration["OperationsIntegration:ReaderUserId"];
        Guid readerId;
        if (string.IsNullOrWhiteSpace(configuredReader))
        {
            if (!Guid.TryParse(http.Request.Headers["X-Dhole-Accepted-By"].ToString(),
                    out readerId) || readerId == Guid.Empty)
                return Results.Problem(title: "CRM acceptance reader identity missing",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        else if (!Guid.TryParse(configuredReader, out readerId) || readerId == Guid.Empty)
        {
            return Results.Problem(title: "CRM acceptance reader identity invalid",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        AuthUserReference? reader;
        try
        {
            reader = await auth.GetActiveAsync(readerId, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Results.Problem(title: "CRM Auth verification unavailable",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        if (reader is null || !CrmOperationsIntegrationAccessPolicy.IsAuthorized(
                reader.Active, reader.Scopes))
            return Results.Forbid();
        return null;
    }

    private static async Task<IResult> ProjectAccountAsync(
        CrmDbContext db, IAuthReferenceService auth, Guid accountId, CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking()
            .Where(a => a.Id == accountId && a.ArchivedAt == null && a.Status == "Active")
            .Select(a => new { a.Id, a.DisplayName, a.ExecutiveUserId,
                a.IsCastroFallasExecutive })
            .SingleOrDefaultAsync(ct);
        if (account is null) return Results.NotFound();
        if (account.ExecutiveUserId == Guid.Empty)
            return Results.Conflict(new { code = "CRM_EXECUTIVE_UNASSIGNED" });

        var executive = await auth.GetActiveAsync(account.ExecutiveUserId, ct);
        if (executive is null || !executive.Active)
            return Results.Conflict(new { code = "CRM_EXECUTIVE_INACTIVE" });

        return Results.Ok(new {
            accountId = account.Id,
            accountName = account.DisplayName,
            executiveUserId = account.ExecutiveUserId,
            executiveName = executive.DisplayName,
            institutionalExecutive = account.IsCastroFallasExecutive
        });
    }
}
