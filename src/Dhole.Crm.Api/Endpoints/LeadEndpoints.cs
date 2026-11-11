using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

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
        group.MapGet("/leads/assigned-executives", AssignedExecutives);
        group.MapGet("/leads/{id:guid}", Get);
        group.MapGet("/leads/{id:guid}/opportunities", ListLeadOpportunities);
        group.MapGet("/leads/{id:guid}/quotes", ListLeadQuotes);
        group.MapPost("/leads", Create);
        group.MapPatch("/leads/{id:guid}", Patch);
        group.MapPost("/leads/{id:guid}/convert", Convert);
        group.MapPost("/leads/{id:guid}/assign", Assign);
        group.MapPost("/leads/{id:guid}/archive", Archive);

        return group;
    }

    // Resolve only salespeople of prospect records visible to the requesting user.
    // Names are fetched server-to-server from Auth, not from a public user directory.
    private static async Task<IResult> AssignedExecutives(
        string? ids, CrmDbContext db, HttpContext http, IAuthReferenceService auth,
        IMemoryCache cache, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "lead")) return CrmAccess.Denied();
        var parts = (ids ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 100 || parts.Any(x => !Guid.TryParse(x, out _)))
            return Results.BadRequest(new { error = "Prospect ids must be 1 to 100 valid GUIDs" });

        var requested = parts.Select(Guid.Parse).Distinct().ToArray();
        var query = db.Leads.AsNoTracking()
            .Where(x => x.ArchivedAt == null && requested.Contains(x.Id));
        if (!CrmAccess.Scope(http, "crm.lead.read-all"))
            query = query.Where(x => x.ExecutiveUserId == CrmAccess.UserId(http));
        var visible = await query.Select(x => new { x.Id, x.ExecutiveUserId }).ToArrayAsync(ct);
        var sellerIds = visible.Where(x => x.ExecutiveUserId != Guid.Empty)
            .Select(x => x.ExecutiveUserId).Distinct().ToArray();

        using var gate = new SemaphoreSlim(4);
        var logger = loggerFactory.CreateLogger("Dhole.Crm.LeadAssignedExecutives");
        var names = await Task.WhenAll(sellerIds.Select(async userId =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var name = await cache.GetOrCreateAsync($"crm:assigned-seller:{userId:N}", async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
                    var person = await auth.GetActiveAsync(userId, ct);
                    return string.IsNullOrWhiteSpace(person?.DisplayName)
                        ? "Vendedor no disponible" : person.DisplayName.Trim();
                });
                return (Id: userId, Name: name ?? "Vendedor no disponible");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not resolve assigned CRM prospect seller");
                return (Id: userId, Name: "Vendedor no disponible");
            }
            finally { gate.Release(); }
        }));
        var resolved = names.ToDictionary(x => x.Id, x => x.Name);
        var sellers = visible.ToDictionary(x => x.Id.ToString("D"),
            x => x.ExecutiveUserId == Guid.Empty ? "Sin asignar"
                : resolved.TryGetValue(x.ExecutiveUserId, out var name)
                    ? name : "Vendedor no disponible");
        return Results.Ok(new { sellers });
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

    private static async Task<IResult> ListLeadOpportunities(Guid id, CrmDbContext db,
        HttpContext http, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "lead") ||
            !CrmDataEndpoints.HasRead(http, "opportunity")) return CrmAccess.Denied();
        var lead = await db.Leads.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        if (lead is null) return Results.NotFound();
        if (!CrmAccess.LeadAccess(http, lead.ExecutiveUserId)) return CrmAccess.Denied();
        var opportunities = db.Opportunities.AsNoTracking()
            .Where(x => x.LeadId == id && x.ArchivedAt == null);
        if (!CrmAccess.Scope(http, "crm.opportunity.read-all"))
            opportunities = opportunities.Where(x => x.ExecutiveUserId == CrmAccess.UserId(http));
        return Results.Ok(await opportunities.OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.Id, x.Title, x.Status, x.ExecutiveUserId })
            .ToListAsync(ct));
    }

    private static async Task<IResult> ListLeadQuotes(Guid id, CrmDbContext db,
        HttpContext http, int? page, int? pageSize, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "lead") ||
            !CrmDataEndpoints.HasRead(http, "opportunity") ||
            !CrmAccess.Scope(http, "crm.quote.read")) return CrmAccess.Denied();
        var lead = await db.Leads.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        if (lead is null) return Results.NotFound();
        if (!CrmAccess.LeadAccess(http, lead.ExecutiveUserId)) return CrmAccess.Denied();
        var opportunities = db.Opportunities.AsNoTracking()
            .Where(x => x.LeadId == id && x.ArchivedAt == null);
        if (!CrmAccess.Scope(http, "crm.opportunity.read-all"))
            opportunities = opportunities.Where(x => x.ExecutiveUserId == CrmAccess.UserId(http));
        var linked = from opportunity in opportunities
                     join quote in db.OpportunityQuotes.AsNoTracking()
                         on opportunity.Id equals quote.OpportunityId
                     select new {
                         quote.Id, quote.PricingQuoteId, quote.PricingRequestId,
                         quote.QuoteNumberSnapshot, quote.CommercialStatusSnapshot,
                         quote.ShipmentModeSnapshot, quote.LinkedAt, quote.LastSyncAt,
                         opportunityId = opportunity.Id, opportunityTitle = opportunity.Title
                     };
        var number = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 25, 1, 100);
        var total = await linked.CountAsync(ct);
        return Results.Ok(new {
            items = await linked.OrderByDescending(x => x.LinkedAt)
                .Skip((number - 1) * size).Take(size).ToListAsync(ct),
            total, page = number, pageSize = size
        });
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
        // Check the entire CRM collection, never only the requesting seller's visible leads.
        // Advisory transaction locks prevent concurrent POSTs from creating the same prospect.
        var normalizedName = LeadDuplicateIdentity.NormalizeName(request.Name);
        var normalizedCompany = LeadDuplicateIdentity.NormalizeName(request.CompanyName);
        var normalizedTax = LeadDuplicateIdentity.NormalizeTax(request.TaxId);
        var compactNames = new[] { normalizedName.Replace(" ", ""), normalizedCompany.Replace(" ", "") }
            .Where(x => x.Length > 0).Distinct().ToArray();
        await using var duplicateGuard = await db.Database.BeginTransactionAsync(ct);
        foreach (var key in LeadDuplicateIdentity.LockKeys(request.Name, request.CompanyName, request.CountryId, request.TaxId))
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(842711, hashtext({key}))", ct);

        var candidates = await db.Leads.AsNoTracking()
            .Where(x => x.ArchivedAt == null &&
                (!request.CountryId.HasValue || x.CountryId == null || x.CountryId == request.CountryId) &&
                (compactNames.Contains(x.Name.Replace(" ", "").ToUpper()) ||
                 (x.CompanyName != null && compactNames.Contains(x.CompanyName.Replace(" ", "").ToUpper())) ||
                 (normalizedTax.Length > 0 && x.TaxId != null &&
                  x.TaxId.Replace(" ", "").Replace("-", "").Replace(".", "").ToUpper() == normalizedTax)))
            .OrderBy(x => x.CreatedAt).ToListAsync(ct);
        var duplicate = candidates.FirstOrDefault(x => LeadDuplicateIdentity.Matches(
            x, request.Name, request.CompanyName, request.CountryId, request.TaxId));
        if (duplicate is not null)
        {
            await duplicateGuard.RollbackAsync(ct);
            string sellerName;
            try
            {
                sellerName = (await auth.GetActiveAsync(duplicate.ExecutiveUserId, ct))?.DisplayName?.Trim()
                    ?? "Vendedor no disponible";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                sellerName = "Vendedor no disponible";
            }
            return Results.Json(new {
                code = "CRM_LEAD_DUPLICATE",
                message = $"El prospecto '{duplicate.Name}' ya existe y pertenece al vendedor {sellerName}.",
                prospectName = duplicate.Name,
                sellerName,
                existingProspectId = duplicate.Id
            }, statusCode: StatusCodes.Status409Conflict);
        }

        var lead = new Lead { Name = request.Name.Trim().ToUpperInvariant(), CompanyName = request.CompanyName?.Trim().ToUpperInvariant(),
            CountryId = request.CountryId, TaxId = request.TaxId?.Trim(),
            SourceCode = request.SourceCode?.Trim(), RelationshipTypeCode = request.RelationshipTypeCode?.Trim(),
            ExecutiveUserId = executiveId, ContactEmail = request.ContactEmail?.Trim(),
            Phone = request.Phone?.Trim(), CreatedByUserId = owner };
        db.Leads.Add(lead);
        await db.SaveChangesAsync(ct);
        await duplicateGuard.CommitAsync(ct);
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
        lead.Name = request.Name.Trim().ToUpperInvariant();
        lead.CompanyName = request.CompanyName?.Trim().ToUpperInvariant();
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
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1000)
            return CrmDataEndpoints.Bad("Archive reason required");
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
            if (!await CrmAccess.CanSeeAccount(db, http, matched.Id, ct)) return CrmAccess.Denied();
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
        var categoryCode = CrmDataEndpoints.CategoryOnCreate(http, request.CategoryType, request.CategoryCode);
        if (string.IsNullOrWhiteSpace(request.CustomerTypeCode) ||
            request.CustomerTypeCode is not ("cash" or "credit") ||
            !CrmDataEndpoints.CategoryValid(request.CategoryType, categoryCode))
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
            CategoryCode = categoryCode, SourceCode = lead.SourceCode,
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
