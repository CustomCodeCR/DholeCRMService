using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

public sealed record CreateContactRequest(string? Name, string? JobTitle, Guid? CountryId, string? Email,
    string? Phone, string RelationRole, bool IsPrimary, string? FirstName = null, string? LastName = null);
public sealed record PatchContactRequest(long ExpectedVersion, string? Name, string? JobTitle,
    string? Email, string? Phone, string? FirstName = null, string? LastName = null,
    Guid? CountryId = null);
public sealed record ContactRelationRequest(string RelationRole, bool IsPrimary);
public sealed record ContactPreferenceRequest(long? ExpectedVersion, string PreferredChannel,
    int PreferredDaysMask, TimeOnly? StartLocalTime, TimeOnly? EndLocalTime,
    string TimezoneId, int? FrequencyDays, bool DoNotContact);
public sealed record ConsentRequest(string Channel, string Purpose, string Status, string Source,
    string? EvidenceReference);
public sealed record AddressRequest(Guid? ContactId, Guid? CountryId, string AddressType,
    string AddressText, string? Location);
public sealed record PatchAddressRequest(long ExpectedVersion, string AddressType,
    string AddressText, string? Location);

internal static class ContactEndpoints
{
    // Never guess first/last name from a legacy full name: compound surnames are ambiguous.
    private static (string Name, string? FirstName, string? LastName)? ContactIdentity(
        string? name, string? firstName, string? lastName)
    {
        var first = firstName?.Trim();
        var last = lastName?.Trim();
        if (firstName is not null || lastName is not null)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(last) ||
                first.Length > 120 || last.Length > 120) return null;
            var combined = first + " " + last;
            return combined.Length <= 220 ? (combined, first, last) : null;
        }
        var existing = name?.Trim();
        return string.IsNullOrWhiteSpace(existing) || existing.Length > 220
            ? null : (existing, null, null);
    }

    public static RouteGroupBuilder MapContacts(this RouteGroupBuilder group)
    {
        group.MapGet("/accounts/{id:guid}/contacts", ListForAccount);
        group.MapPost("/accounts/{id:guid}/contacts", Create);
        group.MapPost("/accounts/{id:guid}/contacts/{contactId:guid}/link", Link);
        group.MapGet("/contacts", ListContacts);
        group.MapGet("/contacts/{id:guid}", Get);
        group.MapGet("/contacts/{id:guid}/accounts", ListLinkedAccounts);
        group.MapPatch("/contacts/{id:guid}", Patch);
        group.MapPost("/contacts/{id:guid}/archive", Archive);
        group.MapGet("/contacts/{id:guid}/preferences", GetPreferences);
        group.MapPut("/contacts/{id:guid}/preferences", PutPreferences);
        group.MapGet("/contacts/{id:guid}/consents", ListConsents);
        group.MapPost("/contacts/{id:guid}/consents", AddConsent);
        group.MapGet("/accounts/{id:guid}/addresses", GetAddresses);
        group.MapPost("/accounts/{id:guid}/addresses", AddAddress);
        group.MapPatch("/addresses/{id:guid}", PatchAddress);
        return group;
    }
    private static async Task<bool> OwnsAccount(Guid id, CrmDbContext db, HttpContext h, CancellationToken ct) =>
        await CrmAccess.CanSeeAccount(db,h,id,ct);
    private static async Task<bool> CanSeeContact(Guid id, CrmDbContext db, HttpContext h, CancellationToken ct) =>
        await (from rel in db.AccountContacts.AsNoTracking()
               join acc in CrmAccess.VisibleAccounts(db,h).AsNoTracking() on rel.AccountId equals acc.Id
               where rel.ContactId == id && rel.ActiveTo == null
               select rel.ContactId).AnyAsync(ct);
    private static async Task<IResult> ListForAccount(Guid id, CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h, "crm.contact.read")) return CrmAccess.Denied();
        if (!await OwnsAccount(id, db, h, ct)) return CrmAccess.Denied();
        return Results.Ok(await (from link in db.AccountContacts.AsNoTracking()
            join contact in db.Contacts.AsNoTracking() on link.ContactId equals contact.Id
            where link.AccountId == id && link.ActiveTo == null && contact.ArchivedAt == null
            orderby link.IsPrimary descending, contact.Name
            select new { contact, link.IsPrimary, link.RelationRole })
            .ToListAsync(ct));
    }
    // Global contact directory is a read-only, paginated projection of relationships
    // visible to the current executive. No cross-account contact leakage.
    private static async Task<IResult> ListContacts(CrmDbContext db, HttpContext h,
        string? q, int? page, int? pageSize, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h, "crm.contact.read")) return CrmAccess.Denied();
        var rows =
            from link in db.AccountContacts.AsNoTracking()
            join account in CrmAccess.VisibleAccounts(db,h).AsNoTracking() on link.AccountId equals account.Id
            join contact in db.Contacts.AsNoTracking() on link.ContactId equals contact.Id
            where link.ActiveTo == null && contact.ArchivedAt == null
            select new { link, account, contact };
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            rows = rows.Where(x => x.contact.Name.ToLower().Contains(term) ||
                (x.contact.Email != null && x.contact.Email.ToLower().Contains(term)) ||
                x.account.DisplayName.ToLower().Contains(term));
        }
        var total = await rows.CountAsync(ct);
        var number = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 25, 1, 100);
        var items = await rows.OrderBy(x => x.contact.Name).ThenBy(x => x.account.DisplayName)
            .ThenBy(x => x.contact.Id).Skip((number - 1) * size).Take(size)
            .Select(x => new {
                id = x.contact.Id, name = x.contact.Name, firstName = x.contact.FirstName,
                lastName = x.contact.LastName, jobTitle = x.contact.JobTitle,
                email = x.contact.Email, phone = x.contact.Phone, status = x.contact.Status,
                accountId = x.account.Id, accountName = x.account.DisplayName,
                relationRole = x.link.RelationRole, isPrimary = x.link.IsPrimary,
            }).ToListAsync(ct);
        return Results.Ok(new { items, total, page = number, pageSize = size });
    }

    // Contact follow-up belongs to its client account. Return only accounts visible
    // to the current user, with no cross-account information leakage.
    private static async Task<IResult> ListLinkedAccounts(Guid id, CrmDbContext db,
        HttpContext h, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h, "crm.contact.read")) return CrmAccess.Denied();
        var linked = await (from link in db.AccountContacts.AsNoTracking()
            join account in CrmAccess.VisibleAccounts(db,h).AsNoTracking() on link.AccountId equals account.Id
            join contact in db.Contacts.AsNoTracking() on link.ContactId equals contact.Id
            where link.ContactId == id && link.ActiveTo == null &&
                contact.ArchivedAt == null
            orderby account.DisplayName
            select new { accountId = account.Id, accountName = account.DisplayName })
            .Distinct().ToListAsync(ct);
        return Results.Ok(linked);
    }

    private static async Task<IResult> Get(Guid id, CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h, "crm.contact.read")) return CrmAccess.Denied();
        if (!await CanSeeContact(id, db, h, ct)) return CrmAccess.Denied();
        var result = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    private static async Task<IResult> Create(Guid id, CreateContactRequest r, CrmDbContext db,
        HttpContext h, IConfigReferenceService config, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h, "crm.contact.create")) return CrmAccess.Denied();
        if (!await OwnsAccount(id, db, h, ct)) return CrmAccess.Denied();
        var identity = ContactIdentity(r.Name, r.FirstName, r.LastName);
        if (identity is null || !CrmDataEndpoints.IsEmailValid(r.Email) ||
            r.Email?.Length > 254 || r.Phone?.Length > 60)
            return CrmDataEndpoints.Bad("First name and surname are required for structured contacts");
        if (!await CrmDataEndpoints.ValidCountry(config, r.CountryId, ct))
            return CrmDataEndpoints.Bad("Unknown country");
        if (r.IsPrimary && await db.AccountContacts.AnyAsync(x => x.AccountId == id &&
            x.IsPrimary && x.ActiveTo == null, ct))
            return CrmDataEndpoints.Conflict("Account already has a primary contact");
        var contact = new Contact { Name = identity.Value.Name,
            FirstName = identity.Value.FirstName, LastName = identity.Value.LastName,
            JobTitle = r.JobTitle?.Trim(),
            CountryId = r.CountryId, Email = r.Email?.Trim().ToLowerInvariant(),
            Phone = r.Phone?.Trim(), CreatedByUserId = CrmAccess.UserId(h) };
        db.Contacts.Add(contact);
        db.AccountContacts.Add(new AccountContact { AccountId = id, ContactId = contact.Id,
            IsPrimary = r.IsPrimary, RelationRole = r.RelationRole });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return CrmDataEndpoints.Conflict("Contact association conflict"); }
        return Results.Created("/api/crm/contacts/" + contact.Id, contact);
    }
    private static async Task<IResult> Link(Guid id, Guid contactId, ContactRelationRequest r,
        CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h, "crm.contact.update")) return CrmAccess.Denied();
        if (!await OwnsAccount(id, db, h, ct) || !await CanSeeContact(contactId, db, h, ct))
            return CrmAccess.Denied();
        var current = await db.AccountContacts.FindAsync([id, contactId], ct);
        if (current is not null && current.ActiveTo == null) return Results.Ok(current);
        if (r.IsPrimary && await db.AccountContacts.AnyAsync(x => x.AccountId == id &&
            x.IsPrimary && x.ActiveTo == null, ct))
            return CrmDataEndpoints.Conflict("Account already has primary contact");
        if (current is not null) { current.ActiveTo = null; current.IsPrimary = r.IsPrimary; current.RelationRole = r.RelationRole; }
        else db.AccountContacts.Add(new AccountContact { AccountId = id, ContactId = contactId,
            IsPrimary = r.IsPrimary, RelationRole = r.RelationRole });
        return await CrmDataEndpoints.Commit(db, ct);
    }
    private static async Task<IResult> Patch(Guid id, PatchContactRequest r, CrmDbContext db,
        HttpContext h, IConfigReferenceService config, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h, "crm.contact.update")) return CrmAccess.Denied();
        if (!await CanSeeContact(id, db, h, ct)) return CrmAccess.Denied();
        var contact = await db.Contacts.FindAsync([id], ct);
        if (contact is null || contact.ArchivedAt != null) return Results.NotFound();
        if (contact.RowVersion != r.ExpectedVersion) return CrmDataEndpoints.Conflict("Stale row version");
        var identity = ContactIdentity(r.Name, r.FirstName, r.LastName);
        if (identity is null || !CrmDataEndpoints.IsEmailValid(r.Email) ||
            r.Email?.Length > 254 || r.Phone?.Length > 60)
            return CrmDataEndpoints.Bad("Invalid contact update");
        var structured = r.FirstName is not null || r.LastName is not null;
        // If an older client changes the full name, clear stale split fields.
        // If the full name has not changed, retain the previously verified split fields.
        if (structured || contact.Name != identity.Value.Name)
        {
            contact.FirstName = identity.Value.FirstName;
            contact.LastName = identity.Value.LastName;
        }
        if (r.CountryId.HasValue &&
            !await CrmDataEndpoints.ValidCountry(config, r.CountryId, ct))
            return CrmDataEndpoints.Bad("Unknown country");
        contact.Name = identity.Value.Name;
        if (r.CountryId.HasValue) contact.CountryId = r.CountryId;
        contact.JobTitle = r.JobTitle?.Trim();
        contact.Email = r.Email?.Trim().ToLowerInvariant();
        contact.Phone = r.Phone?.Trim();
        contact.UpdatedByUserId = CrmAccess.UserId(h);
        contact.UpdatedAt = DateTime.UtcNow;
        contact.RowVersion++;
        var ok = await CrmDataEndpoints.Commit(db, ct);
        return ok is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(contact) : ok;
    }
    private static async Task<IResult> Archive(Guid id, ArchiveAccountRequest r, CrmDbContext db,
        HttpContext h, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h, "crm.contact.archive")) return CrmAccess.Denied();
        if (!await CanSeeContact(id, db, h, ct)) return CrmAccess.Denied();
        var item = await db.Contacts.FindAsync([id], ct);
        if (item is null) return Results.NotFound();
        if (item.RowVersion != r.ExpectedVersion) return CrmDataEndpoints.Conflict("Stale row version");
        item.ArchivedAt = DateTime.UtcNow;
        item.Status = "Archived";
        item.UpdatedAt = DateTime.UtcNow;
        item.UpdatedByUserId = CrmAccess.UserId(h);
        item.RowVersion++;
        var ok = await CrmDataEndpoints.Commit(db, ct);
        return ok is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(item) : ok;
    }
    private static async Task<IResult> GetPreferences(Guid id, CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h, "crm.contact.read")) return CrmAccess.Denied();
        if (!await CanSeeContact(id, db, h, ct)) return CrmAccess.Denied();
        return Results.Ok(await db.ContactPreferences.AsNoTracking().FirstOrDefaultAsync(x => x.ContactId == id, ct));
    }
    private static async Task<IResult> PutPreferences(Guid id, ContactPreferenceRequest r,
        CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h, "crm.contact.update")) return CrmAccess.Denied();
        if (!await CanSeeContact(id, db, h, ct)) return CrmAccess.Denied();
        if (r.PreferredDaysMask is < 0 or > 127 || r.FrequencyDays <= 0 ||
            (r.StartLocalTime is not null && r.EndLocalTime is not null &&
             r.StartLocalTime >= r.EndLocalTime) ||
            !TimeZoneInfo.TryFindSystemTimeZoneById(r.TimezoneId, out _))
            return CrmDataEndpoints.Bad("Invalid contact preferences");
        if (r.PreferredChannel is not ("EMAIL" or "PHONE" or "WHATSAPP" or "VISIT" or "OTHER"))
            return CrmDataEndpoints.Bad("Invalid contact channel");
        var pref = await db.ContactPreferences.SingleOrDefaultAsync(x => x.ContactId == id, ct);
        if (pref is null)
        {
            if (r.ExpectedVersion is not null) return CrmDataEndpoints.Conflict("Preference does not exist");
            pref = new ContactPreference { ContactId = id, CreatedByUserId = CrmAccess.UserId(h) };
            db.ContactPreferences.Add(pref);
        }
        else
        {
            if (r.ExpectedVersion != pref.RowVersion) return CrmDataEndpoints.Conflict("Stale row version");
            pref.RowVersion++;
            pref.UpdatedAt = DateTime.UtcNow;
            pref.UpdatedByUserId = CrmAccess.UserId(h);
        }
        pref.PreferredChannel = r.PreferredChannel;
        pref.PreferredDaysMask = r.PreferredDaysMask;
        pref.StartLocalTime = r.StartLocalTime;
        pref.EndLocalTime = r.EndLocalTime;
        pref.TimezoneId = r.TimezoneId;
        pref.FrequencyDays = r.FrequencyDays;
        pref.DoNotContact = r.DoNotContact;
        var ok = await CrmDataEndpoints.Commit(db, ct);
        return ok is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(pref) : ok;
    }
    private static async Task<IResult> ListConsents(Guid id, CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h, "crm.contact.read")) return CrmAccess.Denied();
        if (!await CanSeeContact(id, db, h, ct)) return CrmAccess.Denied();
        return Results.Ok(await db.MarketingConsents.AsNoTracking().Where(x => x.ContactId == id)
            .OrderByDescending(x => x.CapturedAt).ThenByDescending(x => x.Id).ToListAsync(ct));
    }
    private static async Task<IResult> AddConsent(Guid id, ConsentRequest r,
        CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h, "crm.contact.marketing-consent.manage")) return CrmAccess.Denied();
        if (!await CanSeeContact(id, db, h, ct)) return CrmAccess.Denied();
        if (r.Status is not ("OPTED_IN" or "OPTED_OUT" or "REVOKED") ||
            string.IsNullOrWhiteSpace(r.Channel) || string.IsNullOrWhiteSpace(r.Purpose) ||
            string.IsNullOrWhiteSpace(r.Source) ||
            (r.Status == "OPTED_IN" && string.IsNullOrWhiteSpace(r.EvidenceReference)))
            return CrmDataEndpoints.Bad("Marketing opt-in requires documented evidence");
        var consent = new MarketingConsent {
            ContactId = id, Channel = r.Channel, Purpose = r.Purpose,
            Status = r.Status, Source = r.Source, EvidenceReference = r.EvidenceReference,
            CapturedAt = DateTime.UtcNow, RevokedAt = r.Status == "REVOKED" ? DateTime.UtcNow : null,
            CreatedByUserId = CrmAccess.UserId(h)
        };
        db.MarketingConsents.Add(consent);
        await db.SaveChangesAsync(ct);
        return Results.Created("/api/crm/contacts/" + id + "/consents", consent);
    }
    private static async Task<IResult> GetAddresses(Guid id, CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(h, "account")) return CrmAccess.Denied();
        if (!await OwnsAccount(id, db, h, ct)) return CrmAccess.Denied();
        return Results.Ok(await db.Addresses.AsNoTracking().Where(x => x.AccountId == id)
            .OrderBy(x => x.AddressType).ToListAsync(ct));
    }
    private static async Task<IResult> AddAddress(Guid id, AddressRequest r,
        CrmDbContext db, HttpContext h, IConfigReferenceService config, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h, "crm.account.update")) return CrmAccess.Denied();
        if (!await OwnsAccount(id, db, h, ct)) return CrmAccess.Denied();
        if (string.IsNullOrWhiteSpace(r.AddressText) || r.AddressText.Length > 4000 ||
            string.IsNullOrWhiteSpace(r.AddressType) || r.AddressType.Length > 40)
            return CrmDataEndpoints.Bad("Invalid address");
        if (!await CrmDataEndpoints.ValidCountry(config, r.CountryId, ct))
            return CrmDataEndpoints.Bad("Unknown country");
        if (r.ContactId is not null && !await db.AccountContacts.AnyAsync(x => x.AccountId == id &&
            x.ContactId == r.ContactId && x.ActiveTo == null, ct))
            return CrmDataEndpoints.Bad("Contact does not belong to this account");
        var address = new Address { AccountId = id, ContactId = r.ContactId,
            CountryId = r.CountryId, AddressType = r.AddressType,
            AddressText = r.AddressText.Trim(), Location = r.Location,
            CreatedByUserId = CrmAccess.UserId(h) };
        db.Addresses.Add(address);
        await db.SaveChangesAsync(ct);
        return Results.Created("/api/crm/addresses/" + address.Id, address);
    }
    private static async Task<IResult> PatchAddress(Guid id, PatchAddressRequest r,
        CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h, "crm.account.update")) return CrmAccess.Denied();
        var address = await db.Addresses.FindAsync([id], ct);
        if (address is null) return Results.NotFound();
        if (!await OwnsAccount(address.AccountId, db, h, ct)) return CrmAccess.Denied();
        if (address.RowVersion != r.ExpectedVersion) return CrmDataEndpoints.Conflict("Stale row version");
        if (string.IsNullOrWhiteSpace(r.AddressText) || r.AddressText.Length > 4000)
            return CrmDataEndpoints.Bad("Invalid address");
        address.AddressType = r.AddressType;
        address.AddressText = r.AddressText.Trim();
        address.Location = r.Location;
        address.UpdatedAt = DateTime.UtcNow;
        address.UpdatedByUserId = CrmAccess.UserId(h);
        address.RowVersion++;
        var ok = await CrmDataEndpoints.Commit(db, ct);
        return ok is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(address) : ok;
    }
}
