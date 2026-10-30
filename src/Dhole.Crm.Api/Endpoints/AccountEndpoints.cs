using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Dhole.Crm.Api.Endpoints;

public sealed record CreateAccountRequest(string DisplayName, string LegalName, Guid? CountryId,
    string? TaxIdTypeCode, string? TaxId, string CustomerTypeCode, string? SourceCode,
    string? RelationshipTypeCode, string CategoryType, string CategoryCode, Guid? ExecutiveUserId);
public sealed record PatchAccountRequest(long ExpectedVersion, string DisplayName, string LegalName,
    string CustomerTypeCode, string? SourceCode, string? RelationshipTypeCode,
    string CategoryType, string CategoryCode, bool UpdateFiscalIdentity = false,
    Guid? CountryId = null, string? TaxIdTypeCode = null, string? TaxId = null);
public sealed record AssignAccountRequest(long ExpectedVersion, Guid ExecutiveUserId, string? Reason);
public sealed record ArchiveAccountRequest(long ExpectedVersion);
public sealed record AddBusinessActivityRequest(string ActivityCode, string? Description, string Source);

internal static class AccountEndpoints
{
    public static RouteGroupBuilder MapAccounts(this RouteGroupBuilder group)
    {
        group.MapGet("/accounts", List);
        group.MapGet("/accounts/{id:guid}", Get);
        group.MapPost("/accounts", Create);
        group.MapPatch("/accounts/{id:guid}", Patch);
        group.MapPost("/accounts/{id:guid}/archive", Archive);
        group.MapPost("/accounts/{id:guid}/assign", Assign);
        group.MapGet("/accounts/{id:guid}/assignments", Assignments);
        group.MapGet("/accounts/{id:guid}/business-activities", Activities);
        group.MapPost("/accounts/{id:guid}/business-activities", AddActivity);
        return group;
    }

    private static async Task<IResult> List(CrmDbContext db, HttpContext http,
        string? q, string? customerTypeCode, string? categoryCode, int? page, int? pageSize, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "account")) return CrmAccess.Denied();
        var query = db.Accounts.AsNoTracking().Where(x => x.ArchivedAt == null);
        if (!CrmAccess.Scope(http, "crm.account.read-all"))
            query = query.Where(x => x.ExecutiveUserId == CrmAccess.UserId(http));
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(x => x.DisplayName.ToLower().Contains(q.Trim().ToLower()) ||
                                     x.LegalName.ToLower().Contains(q.Trim().ToLower()));
        if (customerTypeCode is "cash" or "credit")
            query = query.Where(x => x.CustomerTypeCode == customerTypeCode);
        if (categoryCode is "A" or "B" or "C" or "D")
            query = query.Where(x => x.CategoryCode == categoryCode);
        var total = await query.CountAsync(ct);
        var number = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 25, 1, 100);
        var results = await query.OrderBy(x => x.DisplayName).ThenBy(x => x.Id)
            .Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(new { items = results, page = number, pageSize = size, total });
    }
    private static async Task<IResult> Get(Guid id, CrmDbContext db, HttpContext http, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "account")) return CrmAccess.Denied();
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        if (account is null) return Results.NotFound();
        return CrmAccess.AccountAccess(http, account.ExecutiveUserId) ? Results.Ok(account) : CrmAccess.Denied();
    }
    private static async Task<IResult> Create(CreateAccountRequest request, CrmDbContext db,
        HttpContext http, IAuthReferenceService auth, IConfigReferenceService config, IMemoryCache cache, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.account.create")) return CrmAccess.Denied();
        var id = CrmAccess.UserId(http);
        var executiveId = request.ExecutiveUserId ?? id;
        if (executiveId != id && !CrmAccess.Scope(http, "crm.account.assign"))
            return CrmAccess.Denied();
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 220 ||
            string.IsNullOrWhiteSpace(request.LegalName) || request.LegalName.Length > 220 ||
            !CrmDataEndpoints.CategoryValid(request.CategoryType, request.CategoryCode) ||
            request.CustomerTypeCode is not ("cash" or "credit"))
            return CrmDataEndpoints.Bad("Invalid account identity/category/customer type");
        var tax = CrmDataEndpoints.NormalizeTax(request.TaxId);
        if (tax is not null && (tax.Length > 80 || request.CountryId is null ||
                                string.IsNullOrWhiteSpace(request.TaxIdTypeCode)))
            return CrmDataEndpoints.Bad("Tax identifier requires a country and document type");
        if (!await CrmDataEndpoints.ValidCountry(config, request.CountryId, ct))
            return CrmDataEndpoints.Bad("Country not found in Config");
        if (await auth.GetActiveAsync(executiveId, ct) is null) return CrmDataEndpoints.Bad("Inactive or invalid executive");
        if (tax is not null && await db.Accounts.AnyAsync(x => x.CountryId == request.CountryId &&
             x.TaxIdTypeCode == request.TaxIdTypeCode && x.NormalizedTaxId == tax && x.ArchivedAt == null, ct))
            return CrmDataEndpoints.Conflict("Account with the same fiscal identity already exists");
        HaciendaTaxpayerEndpoints.HaciendaTaxpayer? verifiedTaxpayer = null;
        if (tax is not null && request.CountryId.HasValue &&
            cache.TryGetValue("crm:hacienda:ae:" + tax, out HaciendaTaxpayerEndpoints.HaciendaTaxpayer? cached))
        {
            // Only the server's verified snapshot is trusted; never trust client-supplied
            // "verified" flags, timestamps or activities.
            if (cached is not null &&
                string.Equals(cached.Identification, tax, StringComparison.Ordinal))
            {
                if (await config.IsCostaRicaCountryAsync(request.CountryId.Value, ct))
                    verifiedTaxpayer = cached;
            }
        }
        var account = new Account
        {
            CreatedByUserId = id, ExecutiveUserId = executiveId, DisplayName = request.DisplayName.Trim(),
            LegalName = request.LegalName.Trim(), CountryId = request.CountryId,
            TaxIdTypeCode = request.TaxIdTypeCode?.Trim(), TaxId = request.TaxId?.Trim(),
            NormalizedTaxId = tax, CustomerTypeCode = request.CustomerTypeCode,
            SourceCode = request.SourceCode?.Trim(), RelationshipTypeCode = request.RelationshipTypeCode?.Trim(),
            CategoryType = request.CategoryType, CategoryCode = request.CategoryCode,
            VerificationStatus = verifiedTaxpayer is null ? "Pending" : "Verified",
            HaciendaVerifiedAt = verifiedTaxpayer?.VerifiedAt.UtcDateTime
        };
        db.Accounts.Add(account);
        if (verifiedTaxpayer is not null)
        {
            foreach (var activity in verifiedTaxpayer.Activities
                .Where(x => !string.IsNullOrWhiteSpace(x.Code))
                .GroupBy(x => x.Code.Trim(), StringComparer.Ordinal).Select(x => x.First()))
            {
                db.AccountBusinessActivities.Add(new AccountBusinessActivity {
                    AccountId = account.Id, ActivityCode = activity.Code.Trim(),
                    ActivityDescription = activity.Description,
                    Source = "HACIENDA",
                    HaciendaSnapshotAt = verifiedTaxpayer.VerifiedAt.UtcDateTime,
                    CreatedByUserId = id
                });
            }
        }
        db.AccountAssignments.Add(new AccountAssignment { AccountId = account.Id,
            ExecutiveUserId = executiveId, AssignedBy = id, CreatedByUserId = id });
        try
        {
            await db.SaveChangesAsync(ct);
            return Results.Created("/api/crm/accounts/" + account.Id, account);
        }
        catch (DbUpdateException) { return CrmDataEndpoints.Conflict("Account fiscal identity or assignment conflict"); }
    }

    private static async Task<IResult> Patch(Guid id, PatchAccountRequest request, CrmDbContext db,
        HttpContext http, IConfigReferenceService config, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.account.update")) return CrmAccess.Denied();
        var account = await db.Accounts.FindAsync([id], ct);
        if (account is null || account.ArchivedAt is not null) return Results.NotFound();
        if (!CrmAccess.AccountAccess(http, account.ExecutiveUserId)) return CrmAccess.Denied();
        if (account.RowVersion != request.ExpectedVersion) return CrmDataEndpoints.Conflict("Stale row version");
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 220 ||
            string.IsNullOrWhiteSpace(request.LegalName) || request.LegalName.Length > 220 ||
            request.CustomerTypeCode is not ("cash" or "credit") ||
            !CrmDataEndpoints.CategoryValid(request.CategoryType, request.CategoryCode))
            return CrmDataEndpoints.Bad("Invalid account update");
        if (request.UpdateFiscalIdentity)
        {
            var tax = CrmDataEndpoints.NormalizeTax(request.TaxId);
            var taxType = request.TaxIdTypeCode?.Trim();
            if (tax is not null && (tax.Length > 80 || request.CountryId is null ||
                                    string.IsNullOrWhiteSpace(taxType)))
                return CrmDataEndpoints.Bad("Tax identifier requires country and document type");
            if (!await CrmDataEndpoints.ValidCountry(config, request.CountryId, ct))
                return CrmDataEndpoints.Bad("Country not found in Config");

            var changed = account.CountryId != request.CountryId ||
                          account.NormalizedTaxId != tax ||
                          !string.Equals(account.TaxIdTypeCode, taxType, StringComparison.Ordinal);
            if (changed)
            {
                if (tax is not null && await db.Accounts.AnyAsync(x => x.Id != id &&
                    x.CountryId == request.CountryId && x.TaxIdTypeCode == taxType &&
                    x.NormalizedTaxId == tax && x.ArchivedAt == null, ct))
                    return CrmDataEndpoints.Conflict("Account with the same fiscal identity already exists");

                // A verification and imported economic activities belong to the previous ID.
                // Manually entered activities and the account's history are preserved.
                var oldHaciendaActivities = await db.AccountBusinessActivities
                    .Where(x => x.AccountId == id && x.Source == "HACIENDA").ToListAsync(ct);
                db.AccountBusinessActivities.RemoveRange(oldHaciendaActivities);
                account.CountryId = request.CountryId;
                account.TaxIdTypeCode = taxType;
                account.TaxId = request.TaxId?.Trim();
                account.NormalizedTaxId = tax;
                account.VerificationStatus = "Pending";
                account.HaciendaVerifiedAt = null;
            }
        }
        account.DisplayName = request.DisplayName.Trim();
        account.LegalName = request.LegalName.Trim();
        account.CustomerTypeCode = request.CustomerTypeCode;
        account.SourceCode = request.SourceCode?.Trim();
        account.RelationshipTypeCode = request.RelationshipTypeCode?.Trim();
        account.CategoryType = request.CategoryType;
        account.CategoryCode = request.CategoryCode;
        account.RowVersion++;
        account.UpdatedAt = DateTime.UtcNow;
        account.UpdatedByUserId = CrmAccess.UserId(http);
        var committed = await CrmDataEndpoints.Commit(db, ct);
        return committed is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(account) : committed;
    }
    private static async Task<IResult> Archive(Guid id, ArchiveAccountRequest request, CrmDbContext db,
        HttpContext http, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.account.archive")) return CrmAccess.Denied();
        var account = await db.Accounts.FindAsync([id], ct);
        if (account is null) return Results.NotFound();
        if (!CrmAccess.AccountAccess(http, account.ExecutiveUserId)) return CrmAccess.Denied();
        if (account.RowVersion != request.ExpectedVersion) return CrmDataEndpoints.Conflict("Stale row version");
        account.ArchivedAt = DateTime.UtcNow;
        account.Status = "Archived";
        account.UpdatedAt = DateTime.UtcNow;
        account.UpdatedByUserId = CrmAccess.UserId(http);
        account.RowVersion++;
        var committed = await CrmDataEndpoints.Commit(db, ct);
        return committed is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(account) : committed;
    }
    private static async Task<IResult> Assign(Guid id, AssignAccountRequest request, CrmDbContext db,
        HttpContext http, IAuthReferenceService auth, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.account.assign")) return CrmAccess.Denied();
        var account = await db.Accounts.FindAsync([id], ct);
        if (account is null || account.ArchivedAt is not null) return Results.NotFound();
        if (account.RowVersion != request.ExpectedVersion) return CrmDataEndpoints.Conflict("Stale row version");
        if (await auth.GetActiveAsync(request.ExecutiveUserId, ct) is null)
            return CrmDataEndpoints.Bad("Executive is not active in Auth");
        if (account.ExecutiveUserId == request.ExecutiveUserId) return Results.Ok(account);
        var previous = await db.AccountAssignments.SingleOrDefaultAsync(x => x.AccountId == id && x.EffectiveTo == null, ct);
        var when = DateTime.UtcNow;
        if (previous is not null) { previous.EffectiveTo = when; previous.RowVersion++; }
        account.ExecutiveUserId = request.ExecutiveUserId;
        account.UpdatedAt = when;
        account.UpdatedByUserId = CrmAccess.UserId(http);
        account.RowVersion++;
        db.AccountAssignments.Add(new AccountAssignment { AccountId = id,
            ExecutiveUserId = request.ExecutiveUserId, AssignedBy = CrmAccess.UserId(http),
            CreatedByUserId = CrmAccess.UserId(http), EffectiveFrom = when, Reason = request.Reason });
        var committed = await CrmDataEndpoints.Commit(db, ct);
        return committed is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(account) : committed;
    }
    private static async Task<IResult> Assignments(Guid id, CrmDbContext db, HttpContext http, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "account")) return CrmAccess.Denied();
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (account is null) return Results.NotFound();
        if (!CrmAccess.AccountAccess(http, account.ExecutiveUserId)) return CrmAccess.Denied();
        return Results.Ok(await db.AccountAssignments.AsNoTracking().Where(x => x.AccountId == id)
            .OrderByDescending(x => x.EffectiveFrom).ToListAsync(ct));
    }
    private static async Task<IResult> Activities(Guid id, CrmDbContext db, HttpContext http, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "account")) return CrmAccess.Denied();
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (account is null) return Results.NotFound();
        if (!CrmAccess.AccountAccess(http, account.ExecutiveUserId)) return CrmAccess.Denied();
        return Results.Ok(await db.AccountBusinessActivities.AsNoTracking().Where(x => x.AccountId == id)
            .OrderBy(x => x.ActivityCode).ToListAsync(ct));
    }
    private static async Task<IResult> AddActivity(Guid id, AddBusinessActivityRequest request, CrmDbContext db,
        HttpContext http, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.account.update")) return CrmAccess.Denied();
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        if (account is null) return Results.NotFound();
        if (!CrmAccess.AccountAccess(http, account.ExecutiveUserId)) return CrmAccess.Denied();
        if (string.IsNullOrWhiteSpace(request.ActivityCode) || request.ActivityCode.Length > 64)
            return CrmDataEndpoints.Bad("Invalid economic activity code");
        var item = new AccountBusinessActivity { AccountId = id,
            ActivityCode = request.ActivityCode.Trim(), ActivityDescription = request.Description,
            Source = request.Source, CreatedByUserId = CrmAccess.UserId(http) };
        db.AccountBusinessActivities.Add(item);
        var committed = await CrmDataEndpoints.Commit(db, ct);
        return committed is Microsoft.AspNetCore.Http.HttpResults.Ok ?
            Results.Created("/api/crm/accounts/" + id + "/business-activities", item) : committed;
    }
}
