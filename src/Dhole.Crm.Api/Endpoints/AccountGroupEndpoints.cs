using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

/// <summary>
/// A group is an existing primary account and its direct children. Every fiscal identity
/// remains independent. All read models filter members through VisibleAccounts.
/// </summary>
internal static class AccountGroupAccess
{
    public static async Task<Guid[]> VisibleIdsAsync(
        CrmDbContext db, HttpContext http, Guid accountId, CancellationToken ct)
    {
        var anchor = await CrmAccess.VisibleAccounts(db, http).AsNoTracking()
            .Where(a => a.Id == accountId)
            .Select(a => new { a.Id, a.ParentAccountId })
            .SingleOrDefaultAsync(ct);
        if (anchor is null) return [];
        var primaryId = anchor.ParentAccountId ?? anchor.Id;
        return await CrmAccess.VisibleAccounts(db, http).AsNoTracking()
            .Where(a => a.Id == primaryId || a.ParentAccountId == primaryId)
            .Select(a => a.Id).ToArrayAsync(ct);
    }
}

public sealed record LinkAccountGroupMemberRequest(Guid MemberAccountId, long ExpectedVersion);

internal static class AccountGroupEndpoints
{
    public static RouteGroupBuilder MapAccountGroups(this RouteGroupBuilder group)
    {
        group.MapGet("/accounts/{id:guid}/group", Get);
        group.MapPost("/accounts/{id:guid}/group/members", Link);
        group.MapDelete("/accounts/{id:guid}/group/members/{memberId:guid}", Unlink);
        return group;
    }

    private static async Task<IResult> Get(Guid id, CrmDbContext db, HttpContext http,
        CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "account")) return CrmAccess.Denied();
        var anchor = await CrmAccess.VisibleAccounts(db, http).AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new { a.Id, a.ParentAccountId })
            .SingleOrDefaultAsync(ct);
        if (anchor is null) return Results.NotFound();
        var primaryId = anchor.ParentAccountId ?? anchor.Id;
        var members = await CrmAccess.VisibleAccounts(db, http).AsNoTracking()
            .Where(a => a.Id == primaryId || a.ParentAccountId == primaryId)
            .OrderBy(a => a.ParentAccountId != null).ThenBy(a => a.DisplayName)
            .Select(a => new {
                a.Id, a.DisplayName, a.LegalName, a.TaxId,
                a.ParentAccountId, a.RowVersion
            }).ToArrayAsync(ct);
        return Results.Ok(new { primaryId, members });
    }

    // Only a user with global account visibility and update permissions can form
    // groups. A seller can read only the members already visible through CRM ACLs.
    private static bool CanManage(HttpContext http) =>
        CrmAccess.Scope(http, "crm.account.update") &&
        CrmAccess.Scope(http, "crm.account.read-all");

    private static async Task<IResult> Link(Guid id, LinkAccountGroupMemberRequest request,
        CrmDbContext db, HttpContext http, IConfiguration flags, CancellationToken ct)
    {
        if (!CanManage(http)) return CrmAccess.Denied();
        if (request.MemberAccountId == Guid.Empty) return CrmDataEndpoints.Bad("Member account is required");
        var anchor = await db.Accounts.SingleOrDefaultAsync(a => a.Id == id && a.ArchivedAt == null, ct);
        if (anchor is null) return Results.NotFound();
        var primaryId = anchor.ParentAccountId ?? anchor.Id;
        var primary = await db.Accounts.SingleOrDefaultAsync(a => a.Id == primaryId && a.ArchivedAt == null, ct);
        var member = await db.Accounts.SingleOrDefaultAsync(a => a.Id == request.MemberAccountId && a.ArchivedAt == null, ct);
        if (primary is null || member is null) return Results.NotFound();
        if (primary.Id == member.Id) return CrmDataEndpoints.Bad("Primary account cannot be its own member");
        if (member.ParentAccountId == primaryId) return Results.Ok(member);
        if (member.ParentAccountId.HasValue ||
            await db.Accounts.AnyAsync(a => a.ParentAccountId == member.Id, ct))
            return CrmDataEndpoints.Conflict("Account already belongs to a group or is itself a primary account");
        if (member.RowVersion != request.ExpectedVersion)
            return CrmDataEndpoints.Conflict("Account changed; reload before grouping");
        member.ParentAccountId = primaryId;
        member.RowVersion++;
        member.UpdatedAt = DateTime.UtcNow;
        member.UpdatedByUserId = CrmAccess.UserId(http);
        var result = await CrmDataEndpoints.CommitAccount(db, member, http, flags,
            "crm.account.updated.v1", ct);
        return result is IStatusCodeHttpResult { StatusCode: 200 } ? Results.Ok(member) : result;
    }

    private static async Task<IResult> Unlink(Guid id, Guid memberId, long? expectedVersion,
        CrmDbContext db, HttpContext http, IConfiguration flags, CancellationToken ct)
    {
        if (!CanManage(http)) return CrmAccess.Denied();
        var anchor = await db.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == id && a.ArchivedAt == null, ct);
        if (anchor is null) return Results.NotFound();
        var primaryId = anchor.ParentAccountId ?? anchor.Id;
        var member = await db.Accounts.SingleOrDefaultAsync(a =>
            a.Id == memberId && a.ParentAccountId == primaryId && a.ArchivedAt == null, ct);
        if (member is null) return Results.NotFound();
        if (expectedVersion != member.RowVersion)
            return CrmDataEndpoints.Conflict("Account changed; reload before ungrouping");
        member.ParentAccountId = null;
        member.RowVersion++;
        member.UpdatedAt = DateTime.UtcNow;
        member.UpdatedByUserId = CrmAccess.UserId(http);
        var result = await CrmDataEndpoints.CommitAccount(db, member, http, flags,
            "crm.account.updated.v1", ct);
        return result is IStatusCodeHttpResult { StatusCode: 200 } ? Results.NoContent() : result;
    }
}
