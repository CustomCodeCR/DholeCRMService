using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

public sealed record CreateLeadRequest(string Name, string? CompanyName, Guid? CountryId, string? TaxId,
    string? SourceCode, string? RelationshipTypeCode, Guid? ExecutiveUserId,
    string? ContactEmail, string? Phone);
public sealed record PatchLeadRequest(long ExpectedVersion, string Name, string? CompanyName, string? SourceCode,
    string? RelationshipTypeCode, string QualificationStatus, string? ContactEmail, string? Phone);
public sealed record ConvertLeadRequest(Guid? ExistingAccountId, string? Notes,
    string? TaxIdTypeCode, string CustomerTypeCode, string CategoryType, string CategoryCode);
public sealed record AssignLeadRequest(long ExpectedVersion, Guid ExecutiveUserId);

internal static class LeadEndpoints
{
    public static RouteGroupBuilder MapLeads(this RouteGroupBuilder group)
    {
        group.MapGet("/leads", List);
        group.MapGet("/leads/{id:guid}", Get);
        group.MapPost("/leads", Create);
        group.MapPatch("/leads/{id:guid}", Patch);
        group.MapPost("/leads/{id:guid}/convert", Convert);
        group.MapPost("/leads/{id:guid}/assign", Assign);
        group.MapPost("/leads/{id:guid}/archive", Archive);
        return group;
    }
    private static async Task<IResult> List(CrmDbContext db, HttpContext http,
        string? q, int? page, int? pageSize, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "lead")) return CrmAccess.Denied();
        var query = db.Leads.AsNoTracking().Where(x => x.ArchivedAt == null);
        if (!CrmAccess.Scope(http, "crm.lead.read-all"))
            query = query.Where(x => x.ExecutiveUserId == CrmAccess.UserId(http));
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(x => x.Name.ToLower().Contains(q.ToLower()) ||
                                     (x.CompanyName != null && x.CompanyName.ToLower().Contains(q.ToLower())));
        var total = await query.CountAsync(ct);
        var number = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 25, 1, 100);
        return Results.Ok(new { items = await query.OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Skip((number - 1) * size).Take(size).ToListAsync(ct), total, page = number, pageSize = size });
    }
    private static async Task<IResult> Get(Guid id, CrmDbContext db, HttpContext http, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "lead")) return CrmAccess.Denied();
        var lead = await db.Leads.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        if (lead is null) return Results.NotFound();
        return CrmAccess.LeadAccess(http, lead.ExecutiveUserId) ? Results.Ok(lead) : CrmAccess.Denied();
    }
    private static async Task<IResult> Create(CreateLeadRequest request, CrmDbContext db,
        HttpContext http, IAuthReferenceService auth, IConfigReferenceService config, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.lead.create")) return CrmAccess.Denied();
        var owner = CrmAccess.UserId(http);
        var executiveId = request.ExecutiveUserId ?? owner;
        if (executiveId != owner && !CrmAccess.Scope(http, "crm.lead.assign")) return CrmAccess.Denied();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 220 ||
            !CrmDataEndpoints.IsEmailValid(request.ContactEmail) ||
            request.ContactEmail?.Length > 254 || request.Phone?.Length > 60)
            return CrmDataEndpoints.Bad("Invalid lead identity/contact");
        if (!await CrmDataEndpoints.ValidCountry(config, request.CountryId, ct))
            return CrmDataEndpoints.Bad("Unknown country");
        if (await auth.GetActiveAsync(executiveId, ct) is null)
            return CrmDataEndpoints.Bad("Unknown or inactive executive");
        var lead = new Lead { Name = request.Name.Trim(), CompanyName = request.CompanyName?.Trim(),
            CountryId = request.CountryId, TaxId = request.TaxId?.Trim(),
            SourceCode = request.SourceCode?.Trim(), RelationshipTypeCode = request.RelationshipTypeCode?.Trim(),
            ExecutiveUserId = executiveId, ContactEmail = request.ContactEmail?.Trim(),
            Phone = request.Phone?.Trim(), CreatedByUserId = owner };
        db.Leads.Add(lead);
        await db.SaveChangesAsync(ct);
        return Results.Created("/api/crm/leads/" + lead.Id, lead);
    }
    private static async Task<IResult> Patch(Guid id, PatchLeadRequest request, CrmDbContext db,
        HttpContext http, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.lead.update")) return CrmAccess.Denied();
        var lead = await db.Leads.FindAsync([id], ct);
        if (lead is null || lead.ArchivedAt != null) return Results.NotFound();
        if (lead.ExecutiveUserId != CrmAccess.UserId(http) && !CrmAccess.Scope(http, "crm.lead.assign"))
            return CrmAccess.Denied();
        if (lead.ConvertedAt is not null) return CrmDataEndpoints.Conflict("Converted leads are immutable");
        if (lead.RowVersion != request.ExpectedVersion) return CrmDataEndpoints.Conflict("Stale row version");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 220 ||
            !CrmDataEndpoints.IsEmailValid(request.ContactEmail))
            return CrmDataEndpoints.Bad("Invalid lead");
        lead.Name = request.Name.Trim();
        lead.CompanyName = request.CompanyName?.Trim();
        lead.SourceCode = request.SourceCode?.Trim();
        lead.RelationshipTypeCode = request.RelationshipTypeCode?.Trim();
        lead.QualificationStatus = request.QualificationStatus;
        lead.ContactEmail = request.ContactEmail?.Trim();
        lead.Phone = request.Phone?.Trim();
        lead.UpdatedAt = DateTime.UtcNow;
        lead.UpdatedByUserId = CrmAccess.UserId(http);
        lead.RowVersion++;
        var committed = await CrmDataEndpoints.Commit(db, ct);
        return committed is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(lead) : committed;
    }
    private static async Task<IResult> Assign(Guid id, AssignLeadRequest request, CrmDbContext db, HttpContext http,
        IAuthReferenceService auth, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.lead.assign")) return CrmAccess.Denied();
        var lead = await db.Leads.FindAsync([id], ct);
        if (lead is null || lead.ArchivedAt != null) return Results.NotFound();
        if (lead.RowVersion != request.ExpectedVersion) return CrmDataEndpoints.Conflict("Stale row version");
        if (lead.ConvertedAt is not null) return CrmDataEndpoints.Conflict("Converted lead cannot be reassigned");
        if (await auth.GetActiveAsync(request.ExecutiveUserId, ct) is null)
            return CrmDataEndpoints.Bad("Executive not active in Auth");
        lead.ExecutiveUserId = request.ExecutiveUserId;
        lead.UpdatedAt = DateTime.UtcNow;
        lead.UpdatedByUserId = CrmAccess.UserId(http);
        lead.RowVersion++;
        var committed = await CrmDataEndpoints.Commit(db, ct);
        return committed is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(lead) : committed;
    }
    private static async Task<IResult> Archive(Guid id, ArchiveAccountRequest request, CrmDbContext db, HttpContext http,
        CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.lead.archive")) return CrmAccess.Denied();
        var lead = await db.Leads.FindAsync([id], ct);
        if (lead is null) return Results.NotFound();
        if (lead.ExecutiveUserId != CrmAccess.UserId(http) && !CrmAccess.Scope(http, "crm.lead.read-all"))
            return CrmAccess.Denied();
        if (lead.RowVersion != request.ExpectedVersion) return CrmDataEndpoints.Conflict("Stale row version");
        if (lead.ConvertedAt is not null) return CrmDataEndpoints.Conflict("Converted lead cannot be archived");
        lead.ArchivedAt = DateTime.UtcNow;
        lead.UpdatedAt = DateTime.UtcNow;
        lead.UpdatedByUserId = CrmAccess.UserId(http);
        lead.RowVersion++;
        var committed = await CrmDataEndpoints.Commit(db, ct);
        return committed is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(lead) : committed;
    }
    private static async Task<IResult> Convert(Guid id, ConvertLeadRequest request, CrmDbContext db,
        HttpContext http, IConfigReferenceService config, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.lead.convert") || !CrmAccess.Scope(http, "crm.account.create"))
            return CrmAccess.Denied();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var lead = await db.Leads.FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        if (lead is null) return Results.NotFound();
        if (lead.ExecutiveUserId != CrmAccess.UserId(http) && !CrmAccess.Scope(http, "crm.lead.assign"))
            return CrmAccess.Denied();
        if (lead.ConvertedAt is not null)
            return Results.Ok(new { leadId = id, accountId = lead.ConvertedAccountId, alreadyConverted = true });
        if (request.ExistingAccountId is not null)
        {
            var matched = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(
                x => x.Id == request.ExistingAccountId.Value && x.ArchivedAt == null, ct);
            if (matched is null) return Results.NotFound();
            if (!CrmAccess.AccountAccess(http, matched.ExecutiveUserId)) return CrmAccess.Denied();
            // Explicit operator-selected linkage, never fuzzy automatic matching.
            // Require a matching fiscal identity when one exists; no-tax cases require
            // the elevated account assignment scope in addition to own-record access.
            var leadTax = CrmDataEndpoints.NormalizeTax(lead.TaxId);
            if (leadTax is not null &&
                (matched.CountryId != lead.CountryId || matched.NormalizedTaxId != leadTax ||
                 matched.TaxIdTypeCode != request.TaxIdTypeCode))
                return CrmDataEndpoints.Conflict("Existing account fiscal identity does not match prospect");
            if (leadTax is null && !CrmAccess.Scope(http, "crm.account.assign"))
                return CrmAccess.Denied();
            var currentUser = CrmAccess.UserId(http);
            lead.ConvertedAt = DateTime.UtcNow;
            lead.ConvertedAccountId = matched.Id;
            lead.QualificationStatus = "Converted";
            lead.UpdatedAt = DateTime.UtcNow;
            lead.UpdatedByUserId = currentUser;
            lead.RowVersion++;
            db.LeadConversions.Add(new LeadConversion
            {
                LeadId = id, AccountId = matched.Id, ConvertedBy = currentUser,
                CreatedByUserId = currentUser, Notes = request.Notes
            });
            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return Results.Ok(new { leadId = id, accountId = matched.Id, alreadyConverted = false });
            }
            catch (DbUpdateException)
            {
                return CrmDataEndpoints.Conflict("Prospect already converted concurrently");
            }
        }
        if (string.IsNullOrWhiteSpace(request.CustomerTypeCode) ||
            request.CustomerTypeCode is not ("cash" or "credit") ||
            !CrmDataEndpoints.CategoryValid(request.CategoryType, request.CategoryCode))
            return CrmDataEndpoints.Bad("Invalid account classification");
        if (!await CrmDataEndpoints.ValidCountry(config, lead.CountryId, ct))
            return CrmDataEndpoints.Bad("Unknown country");
        var tax = CrmDataEndpoints.NormalizeTax(lead.TaxId);
        if (tax is not null && (lead.CountryId is null || string.IsNullOrWhiteSpace(request.TaxIdTypeCode)))
            return CrmDataEndpoints.Bad("A tax identity needs country and document type");
        if (tax is not null && await db.Accounts.AnyAsync(x => x.CountryId == lead.CountryId &&
            x.TaxIdTypeCode == request.TaxIdTypeCode && x.NormalizedTaxId == tax && x.ArchivedAt == null, ct))
            return CrmDataEndpoints.Conflict("Duplicate fiscal identity: manual resolution required");
        var owner = CrmAccess.UserId(http);
        var account = new Account
        {
            DisplayName = lead.CompanyName ?? lead.Name, LegalName = lead.CompanyName ?? lead.Name,
            CountryId = lead.CountryId, TaxId = lead.TaxId,
            NormalizedTaxId = tax, TaxIdTypeCode = request.TaxIdTypeCode,
            CustomerTypeCode = request.CustomerTypeCode, CategoryType = request.CategoryType,
            CategoryCode = request.CategoryCode, SourceCode = lead.SourceCode,
            RelationshipTypeCode = lead.RelationshipTypeCode, ExecutiveUserId = lead.ExecutiveUserId,
            CreatedByUserId = owner
        };
        db.Accounts.Add(account);
        db.AccountAssignments.Add(new AccountAssignment { AccountId = account.Id,
            ExecutiveUserId = lead.ExecutiveUserId, AssignedBy = owner, CreatedByUserId = owner });
        lead.ConvertedAt = DateTime.UtcNow;
        lead.ConvertedAccountId = account.Id;
        lead.QualificationStatus = "Converted";
        lead.RowVersion++;
        lead.UpdatedAt = DateTime.UtcNow;
        lead.UpdatedByUserId = owner;
        db.LeadConversions.Add(new LeadConversion { LeadId = id, AccountId = account.Id,
            ConvertedBy = owner, CreatedByUserId = owner, Notes = request.Notes });
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Created("/api/crm/accounts/" + account.Id,
                new { leadId = id, accountId = account.Id, alreadyConverted = false });
        }
        catch (DbUpdateException) { return CrmDataEndpoints.Conflict("Concurrent conversion or duplicate fiscal identity"); }
    }
}
