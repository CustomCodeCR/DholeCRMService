using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

/// <summary>
/// Minimal CRM-owned identity projection for the durable Pricing -> Operations
/// acceptance processor. No public account or contact directory is exposed.
/// Configure OperationsIntegration:ReaderUserId manually in the CRM environment:
/// it must be a real active Auth identity with crm.account.read-all.
/// </summary>
internal static class OperationsAccountContractEndpoints
{
    public static IEndpointRouteBuilder MapOperationsAccountContract(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/internal/crm/operations/accounts/{accountId:guid}", async (
            Guid accountId, HttpContext http, IConfiguration configuration,
            IAuthReferenceService auth, CrmDbContext db, CancellationToken ct) =>
        {
            var expectedKey = configuration["Auth:Client:InternalServiceKey"];
            var presentedKey = http.Request.Headers["X-Dhole-Service-Key"].ToString();
            if (string.IsNullOrWhiteSpace(expectedKey) ||
                string.IsNullOrWhiteSpace(presentedKey) ||
                !CrmOperationsIntegrationAccessPolicy.HasServiceKey(expectedKey, presentedKey))
                return Results.Unauthorized();

            if (!Guid.TryParse(configuration["OperationsIntegration:ReaderUserId"], out var readerId)
                || readerId == Guid.Empty)
                return Results.Problem(title: "CRM Operations reader identity not configured",
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            AuthUserReference? serviceActor;
            try
            {
                serviceActor = await auth.GetActiveAsync(readerId, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return Results.Problem(title: "CRM Auth verification unavailable",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (serviceActor is null ||
                !CrmOperationsIntegrationAccessPolicy.IsAuthorized(
                    serviceActor.Active, serviceActor.Scopes))
                return Results.Forbid();

            var account = await db.Accounts.AsNoTracking()
                .Where(a => a.Id == accountId && a.ArchivedAt == null &&
                    a.Status == "Active")
                .Select(a => new { a.Id, a.DisplayName, a.ExecutiveUserId,
                    a.IsCastroFallasExecutive })
                .SingleOrDefaultAsync(ct);
            if (account is null) return Results.NotFound();
            if (account.ExecutiveUserId == Guid.Empty)
                return Results.Conflict(new { code = "CRM_EXECUTIVE_UNASSIGNED" });

            // A removed/inactive executive may not receive a new active case.
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
        });
        return app;
    }
}
