using Dhole.Crm.Infrastructure.Integrations;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

public sealed record HaciendaRetryRequest(long ExpectedVersion);

internal static class CrmHaciendaEndpoints
{
    public static RouteGroupBuilder MapHaciendaVerification(this RouteGroupBuilder g)
    {
        g.MapGet("/accounts/{id:guid}/hacienda", Status);
        g.MapPost("/accounts/{id:guid}/hacienda/retry", Retry);
        return g;
    }

    private static async Task<IResult> Status(Guid id, CrmDbContext db,
        HttpContext h, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(h, "account")) return CrmAccess.Denied();
        var account = await db.Accounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        if (account is null) return Results.NotFound();
        if (!CrmAccess.AccountAccess(h, account.ExecutiveUserId)) return CrmAccess.Denied();
        return Results.Ok(new { account.Id, account.VerificationStatus,
            account.HaciendaVerifiedAt, account.RowVersion });
    }

    private static async Task<IResult> Retry(Guid id, HaciendaRetryRequest request,
        CrmDbContext db, CrmHaciendaVerificationClient verifier,
        HttpContext h, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h, "crm.account.update")) return CrmAccess.Denied();
        var account = await db.Accounts.FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        if (account is null) return Results.NotFound();
        if (!CrmAccess.AccountAccess(h, account.ExecutiveUserId)) return CrmAccess.Denied();
        if (!verifier.Eligible(account))
            return Results.Problem(statusCode: 400, title: "Hacienda validation applies to eligible Costa Rican identification only");
        if (account.RowVersion != request.ExpectedVersion)
            return Results.Problem(statusCode: 409, title: "Account version changed");
        if (account.VerificationStatus == "Pending")
            return Results.Accepted("/api/crm/accounts/" + id + "/hacienda",
                new { account.VerificationStatus });
        account.VerificationStatus = "Pending";
        account.UpdatedAt = DateTime.UtcNow;
        account.UpdatedByUserId = CrmAccess.UserId(h);
        account.RowVersion++;
        await db.SaveChangesAsync(ct);
        return Results.Accepted("/api/crm/accounts/" + id + "/hacienda",
            new { account.VerificationStatus });
    }
}
