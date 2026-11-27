using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

internal static class AccountDivisionEndpoints
{
    public sealed record DivisionRequest(string? Name, Guid[]? ContactIds, long ExpectedVersion);
    public sealed record DivisionSummary(Guid Id, string Name);

    public static RouteGroupBuilder MapAccountDivisions(this RouteGroupBuilder group)
    {
        group.MapGet("/accounts/{id:guid}/divisions", List);
        group.MapPost("/accounts/{id:guid}/divisions", Create);
        group.MapPut("/accounts/{id:guid}/divisions/{divisionId:guid}", Update);
        return group;
    }

    internal static Task<DivisionSummary[]> Summaries(Guid id, CrmDbContext db, CancellationToken ct) =>
        db.Set<AccountDivision>().AsNoTracking().Where(x => x.AccountId == id)
            .OrderBy(x => x.Name).Select(x => new DivisionSummary(x.Id, x.Name)).ToArrayAsync(ct);

    private static IQueryable<AccountDivisionContact> ActiveMemberships(Guid id, CrmDbContext db) =>
        from membership in db.Set<AccountDivisionContact>().AsNoTracking()
        join relation in db.AccountContacts.AsNoTracking()
            on new { membership.AccountId, membership.ContactId } equals new { relation.AccountId, relation.ContactId }
        join contact in db.Contacts.AsNoTracking() on membership.ContactId equals contact.Id
        where membership.AccountId == id && relation.ActiveTo == null && relation.IsOperationsContact
            && contact.ArchivedAt == null && contact.Status == "Active"
        select membership;

    private static async Task<IResult> List(Guid id, CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(h, "account") || !await CrmAccess.CanSeeAccount(db, h, id, ct))
            return CrmAccess.Denied();
        var version = await db.Accounts.Where(x => x.Id == id).Select(x => x.RowVersion).SingleAsync(ct);
        var divisions = await Summaries(id, db, ct);
        var memberships = CrmAccess.Scope(h, "crm.contact.read")
            ? await ActiveMemberships(id, db).ToArrayAsync(ct) : [];
        return Results.Ok(new { accountVersion = version, divisions = divisions.Select(x => new {
            x.Id, x.Name, contactIds = memberships.Where(m => m.DivisionId == x.Id).Select(m => m.ContactId)
        }) });
    }

    private static Task<IResult> Create(Guid id, DivisionRequest request, CrmDbContext db, HttpContext h,
        IConfiguration config, CancellationToken ct) => Save(id, null, request, db, h, config, ct);

    private static Task<IResult> Update(Guid id, Guid divisionId, DivisionRequest request, CrmDbContext db,
        HttpContext h, IConfiguration config, CancellationToken ct) => Save(id, divisionId, request, db, h, config, ct);

    private static async Task<IResult> Save(Guid id, Guid? divisionId, DivisionRequest request, CrmDbContext db,
        HttpContext h, IConfiguration config, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h, "crm.account.update") || !await CrmAccess.CanSeeAccount(db, h, id, ct))
            return CrmAccess.Denied();
        // Null means preserve membership, allowing account-only editors to manage names.
        if (request.ContactIds is not null && (!CrmAccess.Scope(h, "crm.contact.read") ||
            !CrmAccess.Scope(h, "crm.contact.update"))) return CrmAccess.Denied();
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120)
            return CrmDataEndpoints.Bad("Division name required (maximum 120 characters)");
        var normalized = name.ToUpperInvariant();
        if (normalized.Length > 120) return CrmDataEndpoints.Bad("Division name too long");
        var account = await db.Accounts.SingleAsync(x => x.Id == id, ct);
        if (account.RowVersion != request.ExpectedVersion) return CrmDataEndpoints.Conflict("Stale row version");
        var division = divisionId.HasValue
            ? await db.Set<AccountDivision>().SingleOrDefaultAsync(x => x.AccountId == id && x.Id == divisionId, ct)
            : null;
        if (divisionId.HasValue && division is null) return Results.NotFound();
        if (await db.Set<AccountDivision>().AnyAsync(x => x.AccountId == id && x.NormalizedName == normalized &&
            (!divisionId.HasValue || x.Id != divisionId.Value), ct))
            return CrmDataEndpoints.Conflict("Division name already exists in this account");
        var ids = request.ContactIds?.Distinct().ToArray();
        if (ids is not null)
        {
            if (ids.Length > 500 || ids.Contains(Guid.Empty)) return CrmDataEndpoints.Bad("Invalid division contacts");
            var validCount = await (from relation in db.AccountContacts
                join person in db.Contacts on relation.ContactId equals person.Id
                where relation.AccountId == id && relation.ActiveTo == null && relation.IsOperationsContact
                    && person.ArchivedAt == null && person.Status == "Active" && ids.Contains(relation.ContactId)
                select relation.ContactId).CountAsync(ct);
            if (validCount != ids.Length) return CrmDataEndpoints.Bad("Only active operational contacts of this account may be assigned");
        }
        if (division is null)
        {
            division = new AccountDivision { AccountId = id };
            db.Set<AccountDivision>().Add(division);
        }
        division.Name = name;
        division.NormalizedName = normalized;
        if (ids is not null)
        {
            var current = await db.Set<AccountDivisionContact>()
                .Where(x => x.AccountId == id && x.DivisionId == division.Id).ToListAsync(ct);
            db.RemoveRange(current.Where(x => !ids.Contains(x.ContactId)));
            var existing = current.Select(x => x.ContactId).ToHashSet();
            db.AddRange(ids.Where(x => !existing.Contains(x)).Select(contactId =>
                new AccountDivisionContact { AccountId = id, DivisionId = division.Id, ContactId = contactId }));
        }
        account.RowVersion++;
        account.UpdatedAt = DateTime.UtcNow;
        account.UpdatedByUserId = CrmAccess.UserId(h);
        var result = await CrmDataEndpoints.CommitAccount(db, account, h, config, "crm.account.updated.v1", ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(new { division.Id, division.Name, accountVersion = account.RowVersion }) : result;
    }
}
