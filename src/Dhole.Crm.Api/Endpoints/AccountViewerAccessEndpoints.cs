using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

/// <summary>Explicit per-account viewer grants for institutional Castro Fallas customers.</summary>
internal static class AccountViewerAccessEndpoints
{
    public static RouteGroupBuilder MapAccountViewers(this RouteGroupBuilder g)
    {
        g.MapGet("/accounts/{accountId:guid}/viewers", List);
        g.MapPut("/accounts/{accountId:guid}/viewers/{userId:guid}", Grant);
        g.MapDelete("/accounts/{accountId:guid}/viewers/{userId:guid}", Revoke);
        return g;
    }

    private static bool CanAdminister(HttpContext h) => CrmAccess.Scope(h, "crm.account.assign");

    private static async Task<IResult> List(Guid accountId, CrmDbContext db, HttpContext h,
        CancellationToken ct)
    {
        if (!CanAdminister(h)) return CrmAccess.Denied();
        if (!await db.Accounts.AnyAsync(a => a.Id == accountId &&
            a.ArchivedAt == null && a.IsCastroFallasExecutive, ct)) return Results.NotFound();
        return Results.Ok(await db.AccountViewerGrants.AsNoTracking()
            .Where(x => x.AccountId == accountId && x.RevokedAt == null)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new { x.UserId, x.GrantedByUserId, x.CreatedAt })
            .ToArrayAsync(ct));
    }

    private static async Task<IResult> Grant(Guid accountId, Guid userId, CrmDbContext db,
        HttpContext h, IAuthReferenceService auth, CancellationToken ct)
    {
        if (!CanAdminister(h)) return CrmAccess.Denied();
        if (userId == Guid.Empty) return CrmDataEndpoints.Bad("Valid Auth user required");
        if (!await db.Accounts.AnyAsync(a => a.Id == accountId &&
            a.ArchivedAt == null && a.IsCastroFallasExecutive, ct)) return Results.NotFound();
        if (await auth.GetActiveAsync(userId, ct) is null)
            return CrmDataEndpoints.Bad("User inactive or not found in Auth");
        var existing = await db.AccountViewerGrants
            .FirstOrDefaultAsync(x => x.AccountId == accountId &&
                x.UserId == userId && x.RevokedAt == null, ct);
        if (existing is not null) return Results.Ok(new { existing.UserId, existing.AccountId });
        var actor = CrmAccess.UserId(h);
        db.AccountViewerGrants.Add(new AccountViewerGrant
        {
            AccountId = accountId,
            UserId = userId,
            GrantedByUserId = actor,
            CreatedByUserId = actor
        });
        var result = await CrmDataEndpoints.Commit(db, ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok ?
            Results.Ok(new { accountId, userId }) : result;
    }

    private static async Task<IResult> Revoke(Guid accountId, Guid userId, CrmDbContext db,
        HttpContext h, CancellationToken ct)
    {
        if (!CanAdminister(h)) return CrmAccess.Denied();
        var grant = await db.AccountViewerGrants.FirstOrDefaultAsync(
            x => x.AccountId == accountId && x.UserId == userId && x.RevokedAt == null, ct);
        if (grant is null) return Results.NoContent();
        grant.RevokedAt = DateTime.UtcNow;
        grant.UpdatedByUserId = CrmAccess.UserId(h);
        grant.UpdatedAt = DateTime.UtcNow;
        grant.RowVersion++;
        var result = await CrmDataEndpoints.Commit(db, ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok ?
            Results.NoContent() : result;
    }
}
