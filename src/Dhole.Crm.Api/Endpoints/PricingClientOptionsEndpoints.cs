using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

// A single, permission-filtered picker for Pricing; do not query Auth directly from Web.
internal static class PricingClientOptionsEndpoints
{
    private sealed class OptionRow
    {
        public string Kind { get; init; } = "";
        public Guid Id { get; init; }
        public string DisplayName { get; init; } = "";
        public string LegalName { get; init; } = "";
        public string? TaxId { get; init; }
        public Guid? CountryId { get; init; }
        public Guid ExecutiveUserId { get; init; }
        public bool IsCastroFallasExecutive { get; init; }
    }

    public static RouteGroupBuilder MapPricingClientOptions(this RouteGroupBuilder group)
    {
        group.MapGet("/pricing-client-options", List);
        group.MapGet("/operations/accounts/{accountId:guid}", OperationsAccount);
        group.MapGet("/operations/accounts/{accountId:guid}/contacts", OperationsContacts);
        return group;
    }

    private static IQueryable<Dhole.Crm.Domain.Account> OperationalAccounts(CrmDbContext db, HttpContext http) =>
        CrmAccess.Scope(http, "operations.crm.account.select")
            ? db.Accounts.Where(a => a.ArchivedAt == null && a.Status == "Active")
            : CrmAccess.VisibleAccounts(db, http);

    private static async Task<IResult> OperationsAccount(Guid accountId, CrmDbContext db, HttpContext http,
        IAuthReferenceService auth, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "operations.crm.account.select") && !CrmDataEndpoints.HasRead(http, "account"))
            return CrmAccess.Denied();
        var account = await OperationalAccounts(db, http).AsNoTracking().Where(a => a.Id == accountId)
            .Select(a => new { a.Id, a.DisplayName, a.LegalName, a.CountryId, a.ExecutiveUserId, a.IsCastroFallasExecutive })
            .SingleOrDefaultAsync(ct);
        if (account is null) return Results.NotFound();
        var executive = account.IsCastroFallasExecutive ? "Castro Fallas"
            : (await auth.GetActiveAsync(account.ExecutiveUserId, ct))?.DisplayName;
        return Results.Ok(new { account.Id, account.DisplayName, account.LegalName, account.CountryId,
            account.ExecutiveUserId, account.IsCastroFallasExecutive, executiveName = executive,
            divisions = await AccountDivisionEndpoints.Summaries(accountId, db, ct) });
    }

    private static async Task<IResult> OperationsContacts(Guid accountId, CrmDbContext db, HttpContext http, CancellationToken ct, Guid? divisionId, bool? includeDirectory)
    {
        var scoped = CrmAccess.Scope(http, "operations.crm.account.select") &&
            CrmAccess.Scope(http, "operations.crm.contact.read");
        if (!scoped && !CrmAccess.Scope(http, "crm.contact.read")) return CrmAccess.Denied();
        var accounts = scoped ? OperationalAccounts(db, http) : CrmAccess.VisibleAccounts(db, http);
        if (!await accounts.AnyAsync(a => a.Id == accountId, ct)) return CrmAccess.Denied();
        return await ContactEndpoints.ProjectOperationsContactsAsync(accountId, db, ct, divisionId, includeDirectory == true);
    }

    private static async Task<IResult> List(CrmDbContext db, HttpContext http,
        IAuthReferenceService auth, string? mode, string? q, Guid? selectedClientId, Guid? sellerUserId,
        int? page, int? pageSize, CancellationToken ct)
    {
        if (mode is not ("request" or "create" or "operations"))
            return CrmDataEndpoints.Bad("Invalid pricing client mode");
        var operations = mode == "operations";
        var operationsSelect = operations && CrmAccess.Scope(http, "operations.crm.account.select");
        var accountsAllowed = operationsSelect || CrmDataEndpoints.HasRead(http, "account");
        var leadsAllowed = !operations && CrmDataEndpoints.HasRead(http, "lead");
        if (!accountsAllowed && !leadsAllowed) return CrmAccess.Denied();
        if (q?.Length > 220) return CrmDataEndpoints.Bad("Search too long");
        var actor = CrmAccess.UserId(http);
        var leadReadAll = CrmAccess.Scope(http, "crm.lead.read-all");
        if (sellerUserId.HasValue && sellerUserId != actor &&
            !CrmAccess.Scope(http, "crm.account.read-all") &&
            !CrmAccess.Scope(http, "crm.lead.read-all"))
            return CrmAccess.Denied();

        var visible = operationsSelect
            ? db.Accounts.Where(a => a.ArchivedAt == null && a.Status == "Active")
            : CrmAccess.VisibleAccounts(db, http);
        var accounts = visible.AsNoTracking()
            .Where(a => accountsAllowed)
            .Select(a => new OptionRow {
                Kind = "account", Id = a.Id, DisplayName = a.DisplayName,
                LegalName = a.LegalName, TaxId = a.TaxId, CountryId = a.CountryId,
                ExecutiveUserId = a.ExecutiveUserId,
                IsCastroFallasExecutive = a.IsCastroFallasExecutive
            });
        var leads = db.Leads.AsNoTracking()
            .Where(x => leadsAllowed && x.ArchivedAt == null && x.ConvertedAt == null &&
                (leadReadAll || x.ExecutiveUserId == actor))
            .Select(x => new OptionRow {
                Kind = "lead", Id = x.Id, DisplayName = x.CompanyName ?? x.Name,
                LegalName = x.CompanyName ?? x.Name, TaxId = x.TaxId, CountryId = x.CountryId,
                // A lead's operational Auth owner is not its institutional commercial owner.
                // Preserve the flag so Pricing labels Castro Fallas rather than the creator.
                ExecutiveUserId = x.ExecutiveUserId,
                IsCastroFallasExecutive = x.IsCastroFallasExecutive
            });
        var options = accounts.Concat(leads);
        // Quotation editing must re-resolve its linked CRM account/lead by ID.
        // Keep the existing visibility and seller filters: this is not a privileged lookup.
        if (selectedClientId.HasValue)
            options = options.Where(x => x.Id == selectedClientId.Value);
        else if (!string.IsNullOrWhiteSpace(q))
        {
            var search = q.Trim().ToLowerInvariant();
            options = options.Where(x => x.DisplayName.ToLower().Contains(search) ||
                x.LegalName.ToLower().Contains(search) ||
                (x.TaxId != null && x.TaxId.ToLower().Contains(search)));
        }
        if (sellerUserId.HasValue)
            options = options.Where(x => x.IsCastroFallasExecutive ||
                x.ExecutiveUserId == sellerUserId.Value);

        var p = Math.Clamp(page ?? 1, 1, 100000);
        var size = Math.Clamp(pageSize ?? 25, 1, 100);
        var total = await options.CountAsync(ct);
        var rows = await options.OrderBy(x => x.DisplayName).ThenBy(x => x.Id)
            .Skip((p - 1) * size).Take(size).ToArrayAsync(ct);
        var ids = rows.Where(x => !x.IsCastroFallasExecutive && x.ExecutiveUserId != Guid.Empty)
            .Select(x => x.ExecutiveUserId).Distinct().ToArray();
        var names = await Task.WhenAll(ids.Select(async id =>
        {
            var person = await auth.GetActiveAsync(id, ct);
            return (id, name: person?.DisplayName?.Trim() ?? "Vendedor no disponible");
        }));
        var byId = names.ToDictionary(x => x.id, x => x.name);
        return Results.Ok(new {
            items = rows.Select(x => new {
                kind = x.Kind, id = x.Id, displayName = x.DisplayName,
                legalName = x.LegalName, taxId = x.TaxId, countryId = x.CountryId,
                executiveUserId = x.ExecutiveUserId,
                isCastroFallasExecutive = x.IsCastroFallasExecutive,
                executiveName = x.IsCastroFallasExecutive ? "Castro Fallas" :
                    byId.GetValueOrDefault(x.ExecutiveUserId, "Vendedor no disponible")
            }), total, page = p, pageSize = size
        });
    }
}
