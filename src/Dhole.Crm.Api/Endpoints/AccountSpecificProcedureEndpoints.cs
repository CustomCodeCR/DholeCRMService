using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

public sealed record SaveAccountSpecificProcedureRequest(
    string? Name, string? Detail, string? AssignedTeam, long? ExpectedVersion);

internal static class AccountSpecificProcedureEndpoints
{
    public static RouteGroupBuilder MapAccountSpecificProcedures(this RouteGroupBuilder group)
    {
        group.MapGet("/accounts/{id:guid}/specific-procedures", List);
        group.MapPost("/accounts/{id:guid}/specific-procedures", Create);
        group.MapPut("/accounts/{id:guid}/specific-procedures/{procedureId:guid}", Update);
        group.MapDelete("/accounts/{id:guid}/specific-procedures/{procedureId:guid}", Delete);
        return group;
    }

    private static Task<Account?> VisibleAccount(Guid accountId, CrmDbContext db,
        HttpContext http, CancellationToken ct) =>
        CrmAccess.VisibleAccounts(db, http).SingleOrDefaultAsync(a => a.Id == accountId, ct);

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool Valid(SaveAccountSpecificProcedureRequest request) =>
        !string.IsNullOrWhiteSpace(request.Name) &&
        request.Name.Trim().Length <= 160 &&
        (request.Detail is null || request.Detail.Trim().Length <= 8000) &&
        (request.AssignedTeam is null || request.AssignedTeam.Trim().Length <= 500);

    private static async Task<IResult> List(Guid id, CrmDbContext db,
        HttpContext http, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "account")) return CrmAccess.Denied();
        if (await VisibleAccount(id, db, http, ct) is null) return Results.NotFound();

        var procedures = await db.AccountSpecificProcedures.AsNoTracking()
            .Where(p => p.AccountId == id)
            .OrderByDescending(p => p.UpdatedAt)
            .ThenBy(p => p.Name)
            .ToListAsync(ct);
        return Results.Ok(procedures);
    }

    private static async Task<IResult> Commit(CrmDbContext db, Account account,
        HttpContext http, IConfiguration flags, IResult success, CancellationToken ct)
    {
        // Account invalidation is durable through the existing CRM outbox when enabled.
        // In REST-only deployments, both changes still persist in the same SaveChanges.
        account.RowVersion++;
        account.UpdatedAt = DateTime.UtcNow;
        account.UpdatedByUserId = CrmAccess.UserId(http);
        var result = await CrmDataEndpoints.Commit(db, ct);
        return result is IStatusCodeHttpResult { StatusCode: 200 } ? success : result;
    }

    private static async Task<IResult> Create(Guid id, SaveAccountSpecificProcedureRequest request,
        CrmDbContext db, HttpContext http, IConfiguration flags, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.account.update")) return CrmAccess.Denied();
        var account = await VisibleAccount(id, db, http, ct);
        if (account is null) return Results.NotFound();
        if (!Valid(request)) return CrmDataEndpoints.Bad("Procedure name is required (max 160), detail max 8000 and team max 500 characters");

        var procedure = new AccountSpecificProcedure
        {
            AccountId = id,
            Name = request.Name!.Trim(),
            Detail = Trim(request.Detail),
            AssignedTeam = Trim(request.AssignedTeam),
            CreatedByUserId = CrmAccess.UserId(http)
        };
        db.AccountSpecificProcedures.Add(procedure);
        return await Commit(db, account, http, flags,
            Results.Created($"/api/crm/accounts/{id:D}/specific-procedures/{procedure.Id:D}", procedure), ct);
    }

    private static async Task<IResult> Update(Guid id, Guid procedureId,
        SaveAccountSpecificProcedureRequest request, CrmDbContext db,
        HttpContext http, IConfiguration flags, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.account.update")) return CrmAccess.Denied();
        var account = await VisibleAccount(id, db, http, ct);
        if (account is null) return Results.NotFound();
        if (!Valid(request)) return CrmDataEndpoints.Bad("Procedure name is required (max 160), detail max 8000 and team max 500 characters");

        var procedure = await db.AccountSpecificProcedures
            .SingleOrDefaultAsync(p => p.Id == procedureId && p.AccountId == id, ct);
        if (procedure is null) return Results.NotFound();
        if (request.ExpectedVersion is null || request.ExpectedVersion != procedure.RowVersion)
            return CrmDataEndpoints.Conflict("Procedure changed; refresh and try again");

        procedure.Name = request.Name!.Trim();
        procedure.Detail = Trim(request.Detail);
        procedure.AssignedTeam = Trim(request.AssignedTeam);
        procedure.RowVersion++;
        procedure.UpdatedAt = DateTime.UtcNow;
        procedure.UpdatedByUserId = CrmAccess.UserId(http);
        return await Commit(db, account, http, flags, Results.Ok(procedure), ct);
    }

    private static async Task<IResult> Delete(Guid id, Guid procedureId, long? expectedVersion,
        CrmDbContext db, HttpContext http, IConfiguration flags, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.account.update")) return CrmAccess.Denied();
        var account = await VisibleAccount(id, db, http, ct);
        if (account is null) return Results.NotFound();

        var procedure = await db.AccountSpecificProcedures
            .SingleOrDefaultAsync(p => p.Id == procedureId && p.AccountId == id, ct);
        if (procedure is null) return Results.NotFound();
        if (expectedVersion is null || expectedVersion != procedure.RowVersion)
            return CrmDataEndpoints.Conflict("Procedure changed; refresh and try again");

        db.AccountSpecificProcedures.Remove(procedure);
        return await Commit(db, account, http, flags, Results.NoContent(), ct);
    }
}
