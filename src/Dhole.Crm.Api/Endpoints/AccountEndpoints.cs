using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.Realtime;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Dhole.Crm.Api.Endpoints;

public sealed record CreateAccountRequest(string DisplayName, string LegalName, Guid? CountryId,
    string? TaxIdTypeCode, string? TaxId, string CustomerTypeCode, string? SourceCode,
    string? RelationshipTypeCode, string CategoryType, string CategoryCode, Guid? ExecutiveUserId,
    bool AssignToCastroFallas = false,
    List<CreateContactRequest>? Contacts = null, List<AddressRequest>? Addresses = null);
public sealed record PatchAccountRequest(long ExpectedVersion, string DisplayName, string LegalName,
    string CustomerTypeCode, string? SourceCode, string? RelationshipTypeCode,
    string CategoryType, string CategoryCode, bool UpdateFiscalIdentity = false,
    Guid? CountryId = null, string? TaxIdTypeCode = null, string? TaxId = null);
public sealed record AssignAccountRequest(long ExpectedVersion, Guid? ExecutiveUserId, string? Reason,
    bool AssignToCastroFallas = false);
public sealed record ArchiveAccountRequest(long ExpectedVersion, string? Reason = null);
public sealed record SetAccountLogoRequest(long ExpectedVersion, Guid? LogoFileId);
public sealed record AddBusinessActivityRequest(string ActivityCode, string? Description, string Source);
public sealed record UpdateQuoteDeliveryPreferenceRequest(
    string DeliveryTarget, Guid? ContactId, long ExpectedVersion, Guid[]? ContactIds = null);

internal static class AccountEndpoints
{
    public static RouteGroupBuilder MapAccounts(this RouteGroupBuilder group)
    {
        group.MapGet("/accounts", List);
        group.MapGet("/accounts/filter-options", AccountFilterOptions);
        group.MapGet("/accounts/{id:guid}", Get);
        group.MapGet("/accounts/{id:guid}/change-history", ChangeHistory);
        group.MapPost("/accounts", Create);
        group.MapPatch("/accounts/{id:guid}", Patch);
        group.MapPost("/accounts/{id:guid}/archive", Archive);

        group.MapPost("/accounts/{id:guid}/assign", Assign);
        group.MapPatch("/accounts/{id:guid}/logo", SetLogo);
        group.MapGet("/accounts/{id:guid}/assignments", Assignments);
        group.MapGet("/accounts/{id:guid}/assigned-executive", AssignedExecutive);
        group.MapGet("/accounts/{id:guid}/quote-delivery-preference", GetQuoteDeliveryPreference);
        group.MapPut("/accounts/{id:guid}/quote-delivery-preference", PutQuoteDeliveryPreference);
        group.MapGet("/accounts/assigned-executives", AssignedExecutives);
        group.MapGet("/accounts/{id:guid}/business-activities", Activities);
        group.MapPost("/accounts/{id:guid}/business-activities", AddActivity);
        return group;
    }

    private static bool ValidRelationships(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (value.Length > 256) return false;
        var types = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return types.Length <= 10 && types.All(x => x.Length is > 0 and <= 64);
    }

    // Values for CRM filters must come from accounts the requesting user is allowed to see.
    // Do not expose the whole Auth directory to a CRM viewer.
    private static async Task<IResult> AccountFilterOptions(
        CrmDbContext db, HttpContext http, IAuthReferenceService auth,
        IMemoryCache cache, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "account")) return CrmAccess.Denied();
        var assigned = await CrmAccess.VisibleAccounts(db, http).AsNoTracking()
            .Select(x => new { x.ExecutiveUserId, x.IsCastroFallasExecutive })
            .Distinct().ToArrayAsync(ct);
        var sellerIds = assigned.Where(x => !x.IsCastroFallasExecutive && x.ExecutiveUserId != Guid.Empty)
            .Select(x => x.ExecutiveUserId).Distinct().ToArray();
        var logger = loggerFactory.CreateLogger("Dhole.Crm.AccountFilterOptions");
        using var gate = new SemaphoreSlim(4);
        var resolved = await Task.WhenAll(sellerIds.Select(async userId =>
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
                logger.LogWarning(ex, "Could not resolve CRM seller filter option");
                return (Id: userId, Name: "Vendedor no disponible");
            }
            finally { gate.Release(); }
        }));
        var sellers = resolved.Select(x => new { value = x.Id.ToString("D"), label = x.Name })
            .OrderBy(x => x.label).ToList();
        if (assigned.Any(x => x.IsCastroFallasExecutive))
            sellers.Insert(0, new { value = "__castro_fallas__", label = "Castro Fallas" });
        if (assigned.Any(x => !x.IsCastroFallasExecutive && x.ExecutiveUserId == Guid.Empty))
            sellers.Add(new { value = "__unassigned__", label = "Sin asignar" });
        return Results.Ok(new { sellers });
    }

    private static string[] ParseAccountFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] :
        value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static async Task<IResult> List(CrmDbContext db, HttpContext http,
        string? q, string? customerTypeCode, string? categoryCode, string? seller,
        string? relationship, int? page, int? pageSize, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "account")) return CrmAccess.Denied();
        // All dimensions are ANDed; selected values within each dimension are ORed.
        // Filtering stays in SQL before Count/Skip/Take to preserve authorized pagination.
        if (new[] { customerTypeCode, categoryCode, seller, relationship }
            .Any(value => value is { Length: > 4096 }))
            return CrmDataEndpoints.Bad("Account filter too long");

        if (q is { Length: > 220 })
            return CrmDataEndpoints.Bad("Account search term too long");

        var query = CrmAccess.VisibleAccounts(db, http).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLowerInvariant();
            // Identifications are stored normalized without spaces, dots or hyphens.
            // Normalize the search too, supporting partial and formatted document numbers.
            var normalizedTax = CrmDataEndpoints.NormalizeTax(q) ?? "";
            query = query.Where(x => x.DisplayName.ToLower().Contains(term) ||
                x.LegalName.ToLower().Contains(term) ||
                (x.TaxId != null && x.TaxId.ToLower().Contains(term)) ||
                (normalizedTax != "" && x.NormalizedTaxId != null &&
                 x.NormalizedTaxId.Contains(normalizedTax)) ||
                // Keep pre-normalization legacy accounts searchable by cédula.
                (normalizedTax != "" && x.NormalizedTaxId == null && x.TaxId != null &&
                 x.TaxId.ToUpper().Replace("-", "").Replace(".", "").Replace(" ", "").Contains(normalizedTax)));
        }

        var types = ParseAccountFilter(customerTypeCode);
        if (types.Length > 2 || types.Any(value => value is not ("cash" or "credit")))
            return CrmDataEndpoints.Bad("Invalid customer type filter");
        if (types.Length > 0)
            query = query.Where(x => types.Contains(x.CustomerTypeCode));

        var categories = ParseAccountFilter(categoryCode);
        if (categories.Length > 4 || categories.Any(value => value is not ("A" or "B" or "C" or "D")))
            return CrmDataEndpoints.Bad("Invalid account category filter");
        if (categories.Length > 0)
            query = query.Where(x => categories.Contains(x.CategoryCode));

        var sellers = ParseAccountFilter(seller);
        if (sellers.Length > 100) return CrmDataEndpoints.Bad("Too many seller filters");
        if (sellers.Length > 0)
        {
            var house = sellers.Contains("__castro_fallas__");
            var unassigned = sellers.Contains("__unassigned__");
            var sellerIds = new List<Guid>();
            foreach (var value in sellers)
            {
                if (value is "__castro_fallas__" or "__unassigned__") continue;
                if (!Guid.TryParse(value, out var id))
                    return CrmDataEndpoints.Bad("Invalid seller filter");
                sellerIds.Add(id);
            }
            var selectedSellerIds = sellerIds.ToArray();
            query = query.Where(x => (house && x.IsCastroFallasExecutive) ||
                (unassigned && !x.IsCastroFallasExecutive && x.ExecutiveUserId == Guid.Empty) ||
                (!x.IsCastroFallasExecutive && selectedSellerIds.Contains(x.ExecutiveUserId)));
        }

        var relationships = ParseAccountFilter(relationship);
        if (relationships.Length > 10 || relationships.Any(value => value.Length is < 1 or > 64))
            return CrmDataEndpoints.Bad("Invalid relationship filter");
        if (relationships.Length > 0)
        {
            // Account roles are persisted as comma-separated tokens. Match complete roles,
            // never substrings; UNION provides OR semantics translatable by EF Core.
            IQueryable<Account>? matches = null;
            foreach (var value in relationships)
            {
                var token = "," + value.ToLowerInvariant().Replace(" ", "") + ",";
                var matching = query.Where(x => x.RelationshipTypeCode != null &&
                    ("," + x.RelationshipTypeCode.ToLower().Replace(" ", "") + ",").Contains(token));
                matches = matches is null ? matching : matches.Union(matching);
            }
            query = matches!;
        }

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
        return await CrmAccess.CanSeeAccount(db, http, account.Id, ct) ? Results.Ok(account) : CrmAccess.Denied();
    }
    // Resolve the seller through the server-to-server Auth reference so ordinary
    // CRM viewers do not need permission to browse all employee accounts.
    // Resolve visible account sellers in one request. Do not require Auth directory
    // permissions on the client or expose employees assigned to hidden accounts.
    private static async Task<IResult> AssignedExecutives(
        string? ids, CrmDbContext db, HttpContext http, IAuthReferenceService auth,
        IMemoryCache cache, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "account")) return CrmAccess.Denied();
        var parts = (ids ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 100 || parts.Any(x => !Guid.TryParse(x, out _)))
            return Results.BadRequest(new { error = "Account ids must be 1 to 100 valid GUIDs" });
        var requested = parts.Select(Guid.Parse).Distinct().ToArray();
        var visible = await CrmAccess.VisibleAccounts(db, http).AsNoTracking()
            .Where(x => requested.Contains(x.Id))
            .Select(x => new { x.Id, x.ExecutiveUserId, x.IsCastroFallasExecutive })
            .ToArrayAsync(ct);
        var userIds = visible.Where(x => !x.IsCastroFallasExecutive && x.ExecutiveUserId != Guid.Empty)
            .Select(x => x.ExecutiveUserId).Distinct().ToArray();
        using var gate = new SemaphoreSlim(4);
        var logger = loggerFactory.CreateLogger("Dhole.Crm.AccountAssignedExecutives");
        var resolved = await Task.WhenAll(userIds.Select(async userId =>
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
                logger.LogWarning(ex, "Could not resolve assigned CRM seller");
                return (Id: userId, Name: "Vendedor no disponible");
            }
            finally { gate.Release(); }
        }));
        var names = resolved.ToDictionary(x => x.Id, x => x.Name);
        var sellers = visible.ToDictionary(
            x => x.Id.ToString("D"),
            x => x.IsCastroFallasExecutive ? "Castro Fallas"
                : x.ExecutiveUserId == Guid.Empty ? "Sin asignar"
                : names.TryGetValue(x.ExecutiveUserId, out var name) ? name : "Vendedor no disponible");
        return Results.Ok(new { sellers });
    }

    // Read a single account's durable default. The same authenticated CRM endpoint
    // is used by Pricing immediately before every future quote enters the Outbox.
    private static Guid[] ParseQuoteContactIds(string? json, Guid? legacyId)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(json))
            {
                var ids = System.Text.Json.JsonSerializer.Deserialize<Guid[]>(json);
                if (ids is { Length: > 0 })
                    return ids.Where(id => id != Guid.Empty).Distinct().ToArray();
            }
        }
        catch (System.Text.Json.JsonException) { /* Fallback to the legacy contact. */ }
        return legacyId.HasValue && legacyId.Value != Guid.Empty ? [legacyId.Value] : [];
    }

    // Pricing reads the saved account-wide preference for every new quotation.
    private static async Task<IResult> GetQuoteDeliveryPreference(
        Guid id, CrmDbContext db, HttpContext http, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.account.read") &&
            !CrmAccess.Scope(http, "crm.account.read-all"))
            return CrmAccess.Denied();
        var account = await CrmAccess.VisibleAccounts(db, http).AsNoTracking()
            .Where(x => x.Id == id).Select(x => new
            {
                x.QuoteDeliveryTarget, x.QuoteDeliveryContactId, x.QuoteDeliveryContactIdsJson,
                x.QuoteDeliveryUpdatedAtUtc, x.RowVersion
            }).FirstOrDefaultAsync(ct);
        if (account is null) return Results.NotFound();
        var ids = account.QuoteDeliveryTarget == "client"
            ? ParseQuoteContactIds(account.QuoteDeliveryContactIdsJson, account.QuoteDeliveryContactId)
            : Array.Empty<Guid>();
        return Results.Ok(new
        {
            deliveryTarget = account.QuoteDeliveryTarget,
            contactIds = ids,
            contactId = ids.Length > 0 ? (Guid?)ids[0] : null, // Old clients
            updatedAtUtc = account.QuoteDeliveryUpdatedAtUtc,
            rowVersion = account.RowVersion
        });
    }

    private static async Task<IResult> PutQuoteDeliveryPreference(
        Guid id, UpdateQuoteDeliveryPreferenceRequest request,
        CrmDbContext db, HttpContext http, IConfiguration configuration, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.account.update"))
            return CrmAccess.Denied();
        if (request.DeliveryTarget is not ("seller" or "client"))
            return Results.BadRequest(new { error = "Seleccione vendedor o cliente." });

        // Accept legacy contactId in existing REST integrations, but prefer contactIds.
        Guid[] contactIds = request.ContactIds ??
            (request.ContactId is Guid legacyId ? [legacyId] : Array.Empty<Guid>());
        if (contactIds.Length > 100 || contactIds.Contains(Guid.Empty) ||
            contactIds.Distinct().Count() != contactIds.Length)
            return Results.BadRequest(new { error = "La selección contiene contactos duplicados o no válidos." });
        if (request.DeliveryTarget == "client" && contactIds.Length == 0)
            return Results.BadRequest(new { error = "Seleccione al menos un contacto del cliente." });
        if (request.DeliveryTarget == "seller" && contactIds.Length > 0)
            return Results.BadRequest(new { error = "Solo al vendedor no admite contactos." });

        var account = await db.Accounts.FindAsync([id], ct);
        if (account is null || account.ArchivedAt != null) return Results.NotFound();
        if (!await CrmAccess.CanSeeAccount(db, http, id, ct)) return CrmAccess.Denied();
        if (account.RowVersion != request.ExpectedVersion)
            return Results.Conflict(new { error = "La cuenta se actualizó; recargue y vuelva a guardar." });

        if (request.DeliveryTarget == "client")
        {
            var linkedContacts = await (from rel in db.AccountContacts.AsNoTracking()
                join contact in db.Contacts.AsNoTracking() on rel.ContactId equals contact.Id
                where rel.AccountId == id && contactIds.Contains(rel.ContactId) &&
                    rel.ActiveTo == null && contact.ArchivedAt == null &&
                    contact.Status == "Active"
                select new { contact.Id, contact.Email }).ToListAsync(ct);
            if (linkedContacts.Count != contactIds.Length ||
                linkedContacts.Any(item =>
                    string.IsNullOrWhiteSpace(item.Email) ||
                    !System.Net.Mail.MailAddress.TryCreate(item.Email.Trim(), out var mail) ||
                    !string.Equals(mail.Address, item.Email.Trim(), StringComparison.OrdinalIgnoreCase)))
                return Results.BadRequest(new
                {
                    error = "Todos los contactos seleccionados deben pertenecer a la cuenta, estar activos y tener un correo válido."
                });
        }

        account.QuoteDeliveryTarget = request.DeliveryTarget;
        account.QuoteDeliveryContactId = contactIds.Length > 0 ? contactIds[0] : null;
        account.QuoteDeliveryContactIdsJson =
            System.Text.Json.JsonSerializer.Serialize(contactIds);
        account.QuoteDeliveryUpdatedAtUtc = DateTime.UtcNow;
        account.RowVersion++;
        account.UpdatedAt = DateTime.UtcNow;
        account.UpdatedByUserId = CrmAccess.UserId(http);
        if (CrmAccountRealtimeCommit.IsEnabled(configuration))
            await CrmAccountRealtimeCommit.PersistUpdatedAccountAsync(
                db, account, $"crm:account-quote-delivery:{id:N}:{account.RowVersion}", ct);
        else
        {
            var result = await CrmDataEndpoints.Commit(db, ct);
            if (result is not Microsoft.AspNetCore.Http.HttpResults.Ok) return result;
        }
        return Results.Ok(new
        {
            deliveryTarget = account.QuoteDeliveryTarget,
            contactIds,
            contactId = account.QuoteDeliveryContactId,
            updatedAtUtc = account.QuoteDeliveryUpdatedAtUtc,
            rowVersion = account.RowVersion
        });
    }

    private static async Task<IResult> AssignedExecutive(Guid id, CrmDbContext db,
        HttpContext http, IAuthReferenceService auth, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "account")) return CrmAccess.Denied();
        var account = await db.Accounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        if (account is null) return Results.NotFound();
        if (!await CrmAccess.CanSeeAccount(db, http, id, ct)) return CrmAccess.Denied();
        if (account.IsCastroFallasExecutive)
            return Results.Ok(new { displayName = "Castro Fallas", institutional = true });
        if (account.ExecutiveUserId == Guid.Empty)
            return Results.Ok(new { displayName = (string?)null, institutional = false });
        try
        {
            var assigned = await auth.GetActiveAsync(account.ExecutiveUserId, ct);
            return Results.Ok(new {
                displayName = string.IsNullOrWhiteSpace(assigned?.DisplayName)
                    ? (string?)null : assigned.DisplayName,
                institutional = false
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return Results.Json(new { error = "No se pudo consultar el vendedor asignado" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
    private static async Task<IResult> Create(CreateAccountRequest request, CrmDbContext db,
        HttpContext http, IAuthReferenceService auth, IConfigReferenceService config, IMemoryCache cache, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.account.create")) return CrmAccess.Denied();
        var id = CrmAccess.UserId(http);
        var categoryCode = CrmDataEndpoints.CategoryOnCreate(http, request.CategoryType, request.CategoryCode);
        if (request.AssignToCastroFallas && request.ExecutiveUserId.HasValue)
            return CrmDataEndpoints.Bad("Select either Castro Fallas or an Auth executive");
        var executiveId = request.AssignToCastroFallas ? id : request.ExecutiveUserId ?? id;
        if ((executiveId != id || request.AssignToCastroFallas) && !CrmAccess.Scope(http, "crm.account.assign"))
            return CrmAccess.Denied();
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 220 ||
            string.IsNullOrWhiteSpace(request.LegalName) || request.LegalName.Length > 220 ||
            !CrmDataEndpoints.CategoryValid(request.CategoryType, categoryCode) ||
            request.CustomerTypeCode is not ("cash" or "credit"))
            return CrmDataEndpoints.Bad("Invalid account identity/category/customer type");
        if (!ValidRelationships(request.RelationshipTypeCode))
            return CrmDataEndpoints.Bad("Invalid commercial relationship types");
        var tax = CrmDataEndpoints.NormalizeTax(request.TaxId);
        if (tax is not null && (tax.Length > 80 || request.CountryId is null ||
                                string.IsNullOrWhiteSpace(request.TaxIdTypeCode)))
            return CrmDataEndpoints.Bad("Tax identifier requires a country and document type");
        if (!await CrmDataEndpoints.ValidCountry(config, request.CountryId, ct))
            return CrmDataEndpoints.Bad("Country not found in Config");
        // Nested records are optional; validate them before mutating the DbContext.
        // The existing REST endpoints remain available to older clients.
        var newContacts = request.Contacts ?? [];
        var newAddresses = request.Addresses ?? [];
        if (newContacts.Count > 20 || newAddresses.Count > 20)
            return CrmDataEndpoints.Bad("At most 20 contacts and 20 addresses are allowed");
        if (newContacts.Count > 0 && !CrmAccess.Scope(http, "crm.contact.create"))
            return CrmAccess.Denied();
        if (newAddresses.Count > 0 && !CrmAccess.Scope(http, "crm.account.update"))
            return CrmAccess.Denied();
        if (newContacts.Count(x => x.IsPrimary) > 1)
            return CrmDataEndpoints.Bad("Only one primary contact is allowed");
        foreach (var item in newContacts)
        {
            if (string.IsNullOrWhiteSpace(item.FirstName) || string.IsNullOrWhiteSpace(item.LastName) ||
                item.FirstName.Length > 160 || item.LastName.Length > 160 ||
                !CrmDataEndpoints.IsEmailValid(item.Email) || item.Email?.Length > 254 ||
                item.Phone?.Length > 60 || item.JobTitle?.Length > 220 ||
                string.IsNullOrWhiteSpace(item.RelationRole) || item.RelationRole.Length > 60 ||
                !await CrmDataEndpoints.ValidCountry(config, item.CountryId ?? request.CountryId, ct))
                return CrmDataEndpoints.Bad("Invalid optional account contact");
        }
        foreach (var item in newAddresses)
        {
            if (item.ContactId is not null ||
                string.IsNullOrWhiteSpace(item.AddressText) || item.AddressText.Length > 4000 ||
                string.IsNullOrWhiteSpace(item.AddressType) || item.AddressType.Length > 40 ||
                !await CrmDataEndpoints.ValidCountry(config, item.CountryId ?? request.CountryId, ct) ||
                (!string.IsNullOrWhiteSpace(item.CountryCode) &&
                 !System.Text.RegularExpressions.Regex.IsMatch(item.CountryCode, "^[A-Za-z]{2}$")) ||
                new[] { item.Admin1Code, item.Admin1Name, item.Admin2Code,
                    item.Admin2Name, item.Admin3Code, item.Admin3Name }
                    .Any(x => x is { Length: > 160 }) || item.PostalCode is { Length: > 32 } ||
                (!string.IsNullOrWhiteSpace(item.Admin2Code) && string.IsNullOrWhiteSpace(item.Admin1Code)) ||
                (!string.IsNullOrWhiteSpace(item.Admin3Code) && string.IsNullOrWhiteSpace(item.Admin2Code)) ||
                (!string.IsNullOrWhiteSpace(item.Admin1Code) && string.IsNullOrWhiteSpace(item.CountryCode)))
                return CrmDataEndpoints.Bad("Invalid optional account address");
        }
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
            CreatedByUserId = id, ExecutiveUserId = executiveId,
            IsCastroFallasExecutive = request.AssignToCastroFallas,
            DisplayName = request.DisplayName.Trim(),
            LegalName = request.LegalName.Trim(), CountryId = request.CountryId,
            TaxIdTypeCode = request.TaxIdTypeCode?.Trim(), TaxId = request.TaxId?.Trim(),
            NormalizedTaxId = tax, CustomerTypeCode = request.CustomerTypeCode,
            SourceCode = request.SourceCode?.Trim(), RelationshipTypeCode = request.RelationshipTypeCode?.Trim(),
            CategoryType = request.CategoryType, CategoryCode = categoryCode,
            VerificationStatus = verifiedTaxpayer is null ? "Pending" : "Verified",
            HaciendaVerifiedAt = verifiedTaxpayer?.VerifiedAt.UtcDateTime
        };
        db.Accounts.Add(account);
        // Save account, assignments and optional related records in one EF Core
        // transaction. A validation/database failure must not leave an orphan account.
        foreach (var item in newContacts)
        {
            var first = item.FirstName!.Trim();
            var last = item.LastName!.Trim();
            var contact = new Contact {
                FirstName = first, LastName = last, Name = first + " " + last,
                JobTitle = item.JobTitle?.Trim(), CountryId = item.CountryId ?? request.CountryId,
                Email = item.Email?.Trim().ToLowerInvariant(), Phone = item.Phone?.Trim(),
                CreatedByUserId = id
            };
            db.Contacts.Add(contact);
            db.AccountContacts.Add(new AccountContact {
                AccountId = account.Id, ContactId = contact.Id,
                RelationRole = item.RelationRole.Trim(), IsPrimary = item.IsPrimary
            });
        }
        foreach (var item in newAddresses)
        {
            var iso = item.CountryCode?.Trim().ToUpperInvariant();
            db.Addresses.Add(new Address {
                AccountId = account.Id, CountryId = item.CountryId ?? request.CountryId,
                AddressType = item.AddressType.Trim(), AddressText = item.AddressText.Trim(),
                Location = item.Location?.Trim(), CountryCode = iso,
                Admin1Code = item.Admin1Code?.Trim(), Admin1Name = item.Admin1Name?.Trim(),
                Admin2Code = item.Admin2Code?.Trim(), Admin2Name = item.Admin2Name?.Trim(),
                Admin3Code = item.Admin3Code?.Trim(), Admin3Name = item.Admin3Name?.Trim(),
                PostalCode = CrmGeoEndpoints.PostalCodeForCostaRica(iso,
                    item.Admin1Code, item.Admin2Code, item.Admin3Code) ?? item.PostalCode?.Trim(),
                CreatedByUserId = id
            });
        }
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
            ExecutiveUserId = executiveId, IsCastroFallasExecutive = request.AssignToCastroFallas,
            AssignedBy = id, CreatedByUserId = id });
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
        if (!await CrmAccess.CanSeeAccount(db, http, account.Id, ct)) return CrmAccess.Denied();
        if (account.RowVersion != request.ExpectedVersion) return CrmDataEndpoints.Conflict("Stale row version");
        if (!AccountCategoryPolicy.CanUpdate(account.CategoryType, account.CategoryCode,
                request.CategoryType, request.CategoryCode,
                CrmAccess.Scope(http, CrmDataEndpoints.AccountCategoryManageScope)))
            return CrmAccess.Denied();
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
        if (!ValidRelationships(request.RelationshipTypeCode))
            return CrmDataEndpoints.Bad("Invalid commercial relationship types");
        account.DisplayName = request.DisplayName.Trim();
        account.LegalName = request.LegalName.Trim();
        // Credit-backed accounts cannot be silently reset to cash while an
        // approved facility exists; the credit lifecycle owns this transition.
        if (request.CustomerTypeCode == "cash" && await db.CreditProfiles.AsNoTracking()
            .AnyAsync(p => p.AccountId == id && p.Status == "Approved", ct))
            return CrmDataEndpoints.Conflict("El cliente tiene un crédito aprobado; no puede pasarse a contado.");
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
    private static async Task<IResult> SetLogo(Guid id, SetAccountLogoRequest request, CrmDbContext db,
        HttpContext http, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.account.update")) return CrmAccess.Denied();
        var account = await db.Accounts.FindAsync([id], ct);
        if (account is null || account.ArchivedAt is not null) return Results.NotFound();
        if (!await CrmAccess.CanSeeAccount(db, http, account.Id, ct)) return CrmAccess.Denied();
        if (account.RowVersion != request.ExpectedVersion)
            return CrmDataEndpoints.Conflict("Stale row version");
        if (request.LogoFileId == Guid.Empty) return CrmDataEndpoints.Bad("Invalid logo file ID");
        account.LogoFileId = request.LogoFileId;
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
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1000)
            return CrmDataEndpoints.Bad("Archive reason required");
        var account = await db.Accounts.FindAsync([id], ct);
        if (account is null) return Results.NotFound();
        if (!await CrmAccess.CanSeeAccount(db, http, account.Id, ct)) return CrmAccess.Denied();
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
        if (request.AssignToCastroFallas && request.ExecutiveUserId.HasValue)
            return CrmDataEndpoints.Bad("Select either Castro Fallas or an Auth executive");
        if (!request.AssignToCastroFallas &&
            (!request.ExecutiveUserId.HasValue || request.ExecutiveUserId == Guid.Empty))
            return CrmDataEndpoints.Bad("Valid Auth executive required");
        // House assignments have a real accountable owner (the assigning Auth user)
        // while the institutional seller identity is stored separately.
        var executiveId = request.AssignToCastroFallas
            ? CrmAccess.UserId(http) : request.ExecutiveUserId!.Value;
        if (await auth.GetActiveAsync(executiveId, ct) is null)
            return CrmDataEndpoints.Bad("Executive is not active in Auth");
        if (account.ExecutiveUserId == executiveId &&
            account.IsCastroFallasExecutive == request.AssignToCastroFallas)
            return Results.Ok(account);
        var previous = await db.AccountAssignments.SingleOrDefaultAsync(x => x.AccountId == id && x.EffectiveTo == null, ct);
        var when = DateTime.UtcNow;
        if (previous is not null) { previous.EffectiveTo = when; previous.RowVersion++; }
        account.ExecutiveUserId = executiveId;
        account.IsCastroFallasExecutive = request.AssignToCastroFallas;
        account.UpdatedAt = when;
        account.UpdatedByUserId = CrmAccess.UserId(http);
        account.RowVersion++;
        db.AccountAssignments.Add(new AccountAssignment { AccountId = id,
            ExecutiveUserId = executiveId, IsCastroFallasExecutive = request.AssignToCastroFallas,
            AssignedBy = CrmAccess.UserId(http), CreatedByUserId = CrmAccess.UserId(http),
            EffectiveFrom = when, Reason = request.Reason });
        var committed = await CrmDataEndpoints.Commit(db, ct);
        return committed is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(account) : committed;
    }
    private static async Task<IResult> ChangeHistory(Guid id, CrmDbContext db,
        HttpContext http, IAuthReferenceService auth, IMemoryCache cache,
        int? page, int? pageSize, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "account")) return CrmAccess.Denied();
        if (!await CrmAccess.CanSeeAccount(db, http, id, ct)) return Results.NotFound();

        var currentPage = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 15, 1, 50);
        var query = db.AccountChangeHistories.AsNoTracking().Where(x => x.AccountId == id);
        // Account viewers without credit-read permissions cannot inspect financing
        // amounts, payment terms or approval history through the account timeline.
        if (!CrmAccess.Scope(http, "crm.credit.read"))
            query = query.Where(x => x.AreaCode != "CREDIT" && x.AreaCode != "CREDIT_CONTRACT");
        var total = await query.CountAsync(ct);
        var entries = await query.OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id).Skip((currentPage - 1) * size)
            .Take(size).ToListAsync(ct);

        // User names are resolved from Auth only for actors on this visible page.
        // Inactive/deleted users retain their stable identifier as fallback.
        var actors = entries.Where(x => x.ActorUserId.HasValue)
            .Select(x => x.ActorUserId!.Value).Distinct().ToArray();
        var actorNames = await Task.WhenAll(actors.Select(async userId =>
        {
            var cacheKey = "crm:account-audit-actor:" + userId.ToString("N");
            if (!cache.TryGetValue(cacheKey, out string? displayName))
            {
                try
                {
                    var user = await auth.GetActiveAsync(userId, ct);
                    displayName = user?.DisplayName ?? user?.Email ?? "";
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    displayName = "";
                }
                cache.Set(cacheKey, displayName, TimeSpan.FromMinutes(10));
            }
            return (Id: userId, Name: displayName);
        }));
        var namesById = actorNames.ToDictionary(x => x.Id, x => x.Name);

        return Results.Ok(new
        {
            total, page = currentPage, pageSize = size,
            items = entries.Select(x => new
            {
                x.Id, x.ActionCode, x.AreaCode, x.ChangesJson, x.CreatedAt,
                x.ActorUserId,
                actorDisplayName = x.ActorUserId.HasValue &&
                    namesById.TryGetValue(x.ActorUserId.Value, out var displayName) &&
                    !string.IsNullOrWhiteSpace(displayName) ? displayName : null
            })
        });
    }

    private static async Task<IResult> Assignments(Guid id, CrmDbContext db, HttpContext http, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "account")) return CrmAccess.Denied();
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (account is null) return Results.NotFound();
        if (!await CrmAccess.CanSeeAccount(db, http, account.Id, ct)) return CrmAccess.Denied();
        return Results.Ok(await db.AccountAssignments.AsNoTracking().Where(x => x.AccountId == id)
            .OrderByDescending(x => x.EffectiveFrom).ToListAsync(ct));
    }
    private static async Task<IResult> Activities(Guid id, CrmDbContext db, HttpContext http, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "account")) return CrmAccess.Denied();
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (account is null) return Results.NotFound();
        if (!await CrmAccess.CanSeeAccount(db, http, account.Id, ct)) return CrmAccess.Denied();
        return Results.Ok(await db.AccountBusinessActivities.AsNoTracking().Where(x => x.AccountId == id)
            .OrderBy(x => x.ActivityCode).ToListAsync(ct));
    }
    private static async Task<IResult> AddActivity(Guid id, AddBusinessActivityRequest request, CrmDbContext db,
        HttpContext http, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.account.update")) return CrmAccess.Denied();
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        if (account is null) return Results.NotFound();
        if (!await CrmAccess.CanSeeAccount(db, http, account.Id, ct)) return CrmAccess.Denied();
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
