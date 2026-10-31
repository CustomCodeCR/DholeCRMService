using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

/// <summary>Institutional viewer access is global, not duplicated per customer account.</summary>
internal static class CastroFallasGlobalAccessEndpoints
{
    public static RouteGroupBuilder MapCastroFallasGlobalAccess(this RouteGroupBuilder g)
    {
        g.MapGet("/institutional-viewers", List);
        g.MapPut("/institutional-viewers/{userId:guid}", Grant);
        g.MapDelete("/institutional-viewers/{userId:guid}", Revoke);
        return g;
    }
    private static bool Admin(HttpContext h) =>
        CrmAccess.Scope(h, "crm.account.assign");

    private static async Task<IResult> List(CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if (!Admin(h)) return CrmAccess.Denied();
        var grants = await db.CastroFallasViewerGrants.AsNoTracking()
            .Where(x => x.RevokedAt == null)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new { x.UserId, x.GrantedByUserId, x.CreatedAt })
            .ToArrayAsync(ct);
        return Results.Ok(grants);
    }

    private static async Task<IResult> Grant(Guid userId, CrmDbContext db, HttpContext h,
        IAuthReferenceService auth, CancellationToken ct)
    {
        if (!Admin(h)) return CrmAccess.Denied();
        if (userId == Guid.Empty) return CrmDataEndpoints.Bad("Valid Auth user required");
        if (await auth.GetActiveAsync(userId, ct) is null)
            return CrmDataEndpoints.Bad("Only active Auth users may be authorized");
        var existing = await db.CastroFallasViewerGrants.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.RevokedAt == null, ct);
        if (existing is not null) return Results.Ok(new { existing.UserId });
        var actor = CrmAccess.UserId(h);
        db.CastroFallasViewerGrants.Add(new CastroFallasViewerGrant
        {
            UserId = userId, GrantedByUserId = actor, CreatedByUserId = actor
        });
        var result = await CrmDataEndpoints.Commit(db, ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok
            ? Results.Ok(new { userId }) : result;
    }

    private static async Task<IResult> Revoke(Guid userId, CrmDbContext db,
        HttpContext h, CancellationToken ct)
    {
        if (!Admin(h)) return CrmAccess.Denied();
        var grant = await db.CastroFallasViewerGrants.FirstOrDefaultAsync(
            x => x.UserId == userId && x.RevokedAt == null, ct);
        if (grant is null) return Results.NoContent();
        grant.RevokedAt = DateTime.UtcNow;
        grant.UpdatedAt = DateTime.UtcNow;
        grant.UpdatedByUserId = CrmAccess.UserId(h);
        grant.RowVersion++;
        var result = await CrmDataEndpoints.Commit(db, ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok
            ? Results.NoContent() : result;
    }
}
