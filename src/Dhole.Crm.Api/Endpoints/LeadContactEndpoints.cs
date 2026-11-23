using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.Realtime;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

public sealed record PatchLeadContactRequest(long ExpectedVersion, string FirstName, string LastName,
    string? JobTitle, string? Email, string? Phone, Guid? CountryId,
    bool IsPrimary, bool IsOperationsContact);

internal static class LeadContactEndpoints
{
    public static RouteGroupBuilder MapLeadContacts(this RouteGroupBuilder group)
    {
        group.MapGet("/leads/{id:guid}/contacts", List);
        group.MapPost("/leads/{id:guid}/contacts", Create);
        group.MapPatch("/leads/{id:guid}/contacts/{contactId:guid}", Patch);
        group.MapDelete("/leads/{id:guid}/contacts/{contactId:guid}", Remove);
        return group;
    }

    // Contact updates are made on shared Contact rows. Record the prospect-specific
    // event explicitly in the same SaveChanges transaction instead of auditing all contacts.
    private static void RecordContactChange(CrmDbContext db, Guid leadId, Guid actor,
        string action, IEnumerable<AccountFieldChange> fields)
    {
        var changes = fields.Where(x => !string.Equals(x.Before, x.After, StringComparison.Ordinal)).ToList();
        if (changes.Count == 0) return;
        var when = DateTime.UtcNow;
        db.LeadChangeHistories.Add(new LeadChangeHistory
        {
            LeadId = leadId, ActorUserId = actor, CreatedByUserId = actor,
            CreatedAt = when, UpdatedAt = when, ActionCode = action, AreaCode = "CONTACT",
            ChangesJson = System.Text.Json.JsonSerializer.Serialize(changes,
                new System.Text.Json.JsonSerializerOptions
                { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })
        });
    }

    private static async Task<Lead?> VisibleLead(Guid id, CrmDbContext db,
        HttpContext http, CancellationToken ct)
    {
        var lead = await db.Leads.FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        return lead is not null && CrmAccess.LeadAccess(http, lead.ExecutiveUserId) ? lead : null;
    }

    private static async Task<IResult> List(Guid id, CrmDbContext db, HttpContext http, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "lead")) return CrmAccess.Denied();
        if (await VisibleLead(id, db, http, ct) is null) return CrmAccess.Denied();
        var items = await (from link in db.LeadContacts.AsNoTracking()
            join contact in db.Contacts.AsNoTracking() on link.ContactId equals contact.Id
            where link.LeadId == id && link.ActiveTo == null && contact.ArchivedAt == null
            orderby contact.Name
            select new { contact.Id, contact.RowVersion, contact.Name, contact.FirstName, contact.LastName,
                contact.JobTitle, contact.CountryId, contact.Email, contact.Phone,
                link.IsPrimary, link.IsOperationsContact, link.RelationRole }).ToListAsync(ct);
        return Results.Ok(items);
    }

    private static bool Valid(string? first, string? last, string? job, string? email, string? phone) =>
        !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(last) &&
        first.Length <= 120 && last.Length <= 120 &&
        (job is null || job.Length <= 220) &&
        (email is null || email.Length <= 254) &&
        (phone is null || phone.Length <= 60) &&
        CrmDataEndpoints.IsEmailValid(email);

    private static async Task<IResult> Create(Guid id, CreateContactRequest r,
        CrmDbContext db, HttpContext http, IConfigReferenceService config, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.lead.update") ||
            !CrmAccess.Scope(http, "crm.contact.create")) return CrmAccess.Denied();
        var lead = await VisibleLead(id, db, http, ct);
        if (lead is null) return CrmAccess.Denied();
        if (lead.ConvertedAt is not null) return CrmDataEndpoints.Conflict("Converted prospect cannot be edited");
        if (!Valid(r.FirstName, r.LastName, r.JobTitle, r.Email, r.Phone) ||
            !await CrmDataEndpoints.ValidCountry(config, r.CountryId ?? lead.CountryId, ct))
            return CrmDataEndpoints.Bad("Invalid prospect contact");
        if (r.IsPrimary && await db.LeadContacts.AnyAsync(x =>
            x.LeadId == id && x.IsPrimary && x.ActiveTo == null, ct))
            return CrmDataEndpoints.Conflict("Prospect already has a primary contact");
        var owner = CrmAccess.UserId(http);
        var first = r.FirstName!.Trim();
        var last = r.LastName!.Trim();
        var contact = new Contact { FirstName = first, LastName = last, Name = first + " " + last,
            JobTitle = r.JobTitle?.Trim(), CountryId = r.CountryId ?? lead.CountryId,
            Email = r.Email?.Trim().ToLowerInvariant(), Phone = r.Phone?.Trim(),
            CreatedByUserId = owner };
        db.Contacts.Add(contact);
        db.LeadContacts.Add(new LeadContact { LeadId = id, ContactId = contact.Id,
            RelationRole = string.IsNullOrWhiteSpace(r.RelationRole) ? "CONTACT" : r.RelationRole.Trim(),
            IsPrimary = r.IsPrimary, IsOperationsContact = r.IsOperationsContact });
        lead.RowVersion++;
        lead.UpdatedAt = DateTime.UtcNow;
        lead.UpdatedByUserId = owner;
        RecordContactChange(db, id, owner, "CONTACT_CREATED", new[]
        {
            new AccountFieldChange("ContactName", null, contact.Name),
            new AccountFieldChange("ContactEmail", null, contact.Email),
            new AccountFieldChange("ContactPhone", null, contact.Phone)
        });
        try
        {
            await CrmCollectionRealtimeCommit.SaveLeadAsync(db,
                http.RequestServices.GetRequiredService<IConfiguration>(), http.TraceIdentifier, ct);
        }
        catch (DbUpdateException) { return CrmDataEndpoints.Conflict("Contact association conflict"); }
        return Results.Created("/api/crm/leads/" + id + "/contacts", contact);
    }

    private static async Task<IResult> Patch(Guid id, Guid contactId, PatchLeadContactRequest r,
        CrmDbContext db, HttpContext http, IConfigReferenceService config, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.lead.update") ||
            !CrmAccess.Scope(http, "crm.contact.update")) return CrmAccess.Denied();
        var lead = await VisibleLead(id, db, http, ct);
        if (lead is null) return CrmAccess.Denied();
        if (lead.ConvertedAt is not null) return CrmDataEndpoints.Conflict("Converted prospect cannot be edited");
        var link = await db.LeadContacts.FirstOrDefaultAsync(x =>
            x.LeadId == id && x.ContactId == contactId && x.ActiveTo == null, ct);
        if (link is null) return Results.NotFound();
        var contact = await db.Contacts.FirstOrDefaultAsync(x => x.Id == contactId && x.ArchivedAt == null, ct);
        if (contact is null) return Results.NotFound();
        if (contact.RowVersion != r.ExpectedVersion) return CrmDataEndpoints.Conflict("Stale contact version");
        if (!Valid(r.FirstName, r.LastName, r.JobTitle, r.Email, r.Phone) ||
            !await CrmDataEndpoints.ValidCountry(config, r.CountryId ?? lead.CountryId, ct))
            return CrmDataEndpoints.Bad("Invalid prospect contact");
        if (r.IsPrimary && !link.IsPrimary && await db.LeadContacts.AnyAsync(x =>
            x.LeadId == id && x.ContactId != contactId && x.IsPrimary && x.ActiveTo == null, ct))
            return CrmDataEndpoints.Conflict("Prospect already has a primary contact");
        var previousName = contact.Name;
        var previousEmail = contact.Email;
        var previousPhone = contact.Phone;
        var previousJobTitle = contact.JobTitle;
        var previousPrimary = link.IsPrimary;
        var previousOperations = link.IsOperationsContact;
        contact.FirstName = r.FirstName.Trim();
        contact.LastName = r.LastName.Trim();
        contact.Name = contact.FirstName + " " + contact.LastName;
        contact.JobTitle = r.JobTitle?.Trim();
        contact.Email = r.Email?.Trim().ToLowerInvariant();
        contact.Phone = r.Phone?.Trim();
        contact.CountryId = r.CountryId ?? lead.CountryId;
        contact.RowVersion++;
        contact.UpdatedAt = DateTime.UtcNow;
        contact.UpdatedByUserId = CrmAccess.UserId(http);
        link.IsPrimary = r.IsPrimary;
        link.IsOperationsContact = r.IsOperationsContact;
        lead.RowVersion++;
        lead.UpdatedAt = DateTime.UtcNow;
        lead.UpdatedByUserId = CrmAccess.UserId(http);
        RecordContactChange(db, id, CrmAccess.UserId(http), "CONTACT_UPDATED", new[]
        {
            new AccountFieldChange("ContactName", previousName, contact.Name),
            new AccountFieldChange("ContactEmail", previousEmail, contact.Email),
            new AccountFieldChange("ContactPhone", previousPhone, contact.Phone),
            new AccountFieldChange("JobTitle", previousJobTitle, contact.JobTitle),
            new AccountFieldChange("IsPrimary", previousPrimary ? "true" : "false", link.IsPrimary ? "true" : "false"),
            new AccountFieldChange("IsOperationsContact", previousOperations ? "true" : "false", link.IsOperationsContact ? "true" : "false")
        });
        try
        {
            await CrmCollectionRealtimeCommit.SaveLeadAsync(db,
                http.RequestServices.GetRequiredService<IConfiguration>(), http.TraceIdentifier, ct);
        }
        catch (DbUpdateException) { return CrmDataEndpoints.Conflict("Prospect contact update conflict"); }
        return Results.Ok(contact);
    }

    private static async Task<IResult> Remove(Guid id, Guid contactId,
        CrmDbContext db, HttpContext http, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.lead.update") ||
            !CrmAccess.Scope(http, "crm.contact.update")) return CrmAccess.Denied();
        var lead = await VisibleLead(id, db, http, ct);
        if (lead is null) return CrmAccess.Denied();
        if (lead.ConvertedAt is not null) return CrmDataEndpoints.Conflict("Converted prospect cannot be edited");
        var link = await db.LeadContacts.FirstOrDefaultAsync(x =>
            x.LeadId == id && x.ContactId == contactId && x.ActiveTo == null, ct);
        if (link is null) return Results.NotFound();
        var contactName = await db.Contacts.AsNoTracking()
            .Where(x => x.Id == contactId).Select(x => x.Name).FirstOrDefaultAsync(ct);
        link.ActiveTo = DateTime.UtcNow;
        lead.RowVersion++;
        lead.UpdatedAt = DateTime.UtcNow;
        lead.UpdatedByUserId = CrmAccess.UserId(http);
        RecordContactChange(db, id, CrmAccess.UserId(http), "CONTACT_REMOVED",
            new[] { new AccountFieldChange("ContactName", contactName ?? "Contacto", null) });
        await CrmCollectionRealtimeCommit.SaveLeadAsync(db,
            http.RequestServices.GetRequiredService<IConfiguration>(), http.TraceIdentifier, ct);
        return Results.NoContent();
    }
}
