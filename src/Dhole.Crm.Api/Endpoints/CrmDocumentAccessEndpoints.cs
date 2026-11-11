using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

/// <summary>
/// Verifies CRM record-level permissions before a client uses Storage's
/// entity-bound file APIs. Storage retains responsibility for file scopes.
/// This endpoint never exposes file contents or cross-account relationships.
/// </summary>
internal static class CrmDocumentAccessEndpoints
{
    public static RouteGroupBuilder MapCrmDocuments(this RouteGroupBuilder group)
    {
        group.MapGet("/documents/{kind}/{id:guid}/access", Access);
        return group;
    }

    private static async Task<IResult> Access(string kind, Guid id,
        CrmDbContext db, HttpContext http, CancellationToken ct)
    {
        if (id == Guid.Empty) return Results.NotFound();
        var userId = CrmAccess.UserId(http);
        var crossAgenda = CrmAccess.CanManageAgendaAcrossUsers(http);
        bool allowed;
        bool upload;

        switch (kind.ToLowerInvariant())
        {
            case "account":
                allowed = CrmDataEndpoints.HasRead(http, "account")
                    && await CrmAccess.CanSeeAccount(db, http, id, ct);
                upload = CrmAccess.Scope(http, "crm.account.update");
                break;
            case "procedure":
                // A procedure inherits the visibility of its parent account.
                // Never reveal attachments for an inaccessible or deleted record.
                var procedureAccountId = await db.AccountSpecificProcedures.AsNoTracking()
                    .Where(x => x.Id == id)
                    .Select(x => (Guid?)x.AccountId)
                    .SingleOrDefaultAsync(ct);
                allowed = procedureAccountId.HasValue
                    && CrmDataEndpoints.HasRead(http, "account")
                    && await CrmAccess.CanSeeAccount(db, http, procedureAccountId.Value, ct);
                upload = CrmAccess.Scope(http, "crm.account.update");
                break;
            case "lead":
                allowed = CrmDataEndpoints.HasRead(http, "lead")
                    && await db.Leads.AsNoTracking().AnyAsync(x => x.Id == id
                        && x.ArchivedAt == null
                        && (CrmAccess.Scope(http, "crm.lead.read-all")
                            || x.ExecutiveUserId == userId), ct);
                upload = CrmAccess.Scope(http, "crm.lead.update");
                break;
            case "contact":
                allowed = CrmAccess.Scope(http, "crm.contact.read")
                    && await (from link in db.AccountContacts.AsNoTracking()
                        join account in CrmAccess.VisibleAccounts(db, http).AsNoTracking()
                            on link.AccountId equals account.Id
                        join contact in db.Contacts.AsNoTracking()
                            on link.ContactId equals contact.Id
                        where contact.Id == id && contact.ArchivedAt == null
                            && link.ActiveTo == null
                        select contact.Id).AnyAsync(ct);
                upload = CrmAccess.Scope(http, "crm.contact.update");
                break;
            case "activity":
                allowed = (CrmDataEndpoints.HasRead(http, "activity") || crossAgenda)
                    && await db.Activities.AsNoTracking().AnyAsync(x => x.Id == id
                        && x.ArchivedAt == null
                        && (crossAgenda || x.OwnerUserId == userId), ct);
                if (allowed)
                {
                    var accountId = await db.Activities.AsNoTracking()
                        .Where(x => x.Id == id).Select(x => x.AccountId)
                        .FirstAsync(ct);
                    allowed = !accountId.HasValue
                        || await CrmAccess.CanSeeAccount(db, http, accountId.Value, ct);
                }
                upload = CrmAccess.Scope(http, "crm.activity.update")
                    || CrmAccess.Scope(http, "crm.activity.create");
                break;
            case "followup":
                allowed = (CrmAccess.Scope(http, "crm.followup.manage")
                    || CrmAccess.Scope(http, "crm.task.manage")
                    || CrmDataEndpoints.HasRead(http, "activity")
                    || crossAgenda || CrmAccess.Scope(http, "crm.account.read-all"))
                    && await db.Followups.AsNoTracking().AnyAsync(x => x.Id == id
                        && x.ArchivedAt == null
                        && (crossAgenda || x.AssignedUserId == userId
                            || (x.AccountId.HasValue
                                && CrmAccess.Scope(http, "crm.account.read-all"))
                            || (x.LeadId.HasValue
                                && CrmAccess.Scope(http, "crm.lead.read-all"))), ct);
                if (allowed)
                {
                    var accountId = await db.Followups.AsNoTracking()
                        .Where(x => x.Id == id).Select(x => x.AccountId)
                        .FirstAsync(ct);
                    allowed = !accountId.HasValue
                        || await CrmAccess.CanSeeAccount(db, http, accountId.Value, ct);
                }
                upload = CrmAccess.Scope(http, "crm.followup.manage")
                    || CrmAccess.Scope(http, "crm.task.manage");
                break;
            default:
                return Results.NotFound();
        }

        if (!allowed) return CrmAccess.Denied();
        return Results.Ok(new { canUpload = upload });
    }
}
