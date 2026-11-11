using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Net.Mail;

namespace Dhole.Crm.Api.Endpoints;

public sealed record SetAccountQuoteDeliveryRequest(long ExpectedVersion, string DeliveryTarget, Guid? ContactId);

internal static class AccountQuoteDeliveryEndpoints
{
    internal static RouteGroupBuilder MapAccountQuoteDelivery(this RouteGroupBuilder group)
    {
        group.MapGet("/accounts/{id:guid}/quote-delivery", Get);
        group.MapPut("/accounts/{id:guid}/quote-delivery", Put);
        return group;
    }

    private static async Task<IResult> Get(Guid id, CrmDbContext db, HttpContext http, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(http, "account")) return CrmAccess.Denied();
        if (!await CrmAccess.CanSeeAccount(db, http, id, ct)) return CrmAccess.Denied();
        var account = await db.Accounts.AsNoTracking()
            .Where(x => x.Id == id && x.ArchivedAt == null)
            .Select(x => new { x.QuoteDeliveryTarget, x.QuoteDeliveryContactId, x.RowVersion })
            .FirstOrDefaultAsync(ct);
        return account is null ? Results.NotFound() : Results.Ok(new {
            deliveryTarget = account.QuoteDeliveryTarget,
            contactId = account.QuoteDeliveryContactId,
            rowVersion = account.RowVersion
        });
    }

    private static async Task<IResult> Put(Guid id, SetAccountQuoteDeliveryRequest request,
        CrmDbContext db, HttpContext http, CancellationToken ct)
    {
        if (!CrmAccess.Scope(http, "crm.account.update")) return CrmAccess.Denied();
        if (!await CrmAccess.CanSeeAccount(db, http, id, ct)) return CrmAccess.Denied();
        var account = await db.Accounts.FirstOrDefaultAsync(
            x => x.Id == id && x.ArchivedAt == null, ct);
        if (account is null) return Results.NotFound();
        if (account.RowVersion != request.ExpectedVersion)
            return CrmDataEndpoints.Conflict("La cuenta cambió; actualice y vuelva a guardar.");

        var target = request.DeliveryTarget?.Trim().ToLowerInvariant();
        if (target is not ("seller" or "client"))
            return CrmDataEndpoints.Bad("Seleccione vendedor o cliente.");
        if (target == "seller" && request.ContactId.HasValue)
            return CrmDataEndpoints.Bad("No se permite contacto en el modo solo vendedor.");
        if (target == "client")
        {
            if (request.ContactId is not { } contactId || contactId == Guid.Empty)
                return CrmDataEndpoints.Bad("Seleccione el contacto para las próximas cotizaciones.");
            var email = await (from link in db.AccountContacts.AsNoTracking()
                join contact in db.Contacts.AsNoTracking() on link.ContactId equals contact.Id
                where link.AccountId == id && link.ContactId == contactId &&
                    link.ActiveTo == null && contact.ArchivedAt == null
                select contact.Email).FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(email) ||
                !MailAddress.TryCreate(email, out var validEmail) ||
                !string.Equals(validEmail.Address, email.Trim(), StringComparison.OrdinalIgnoreCase))
                return CrmDataEndpoints.Bad(
                    "El contacto debe estar vinculado a esta cuenta y tener correo válido.");
        }

        if (account.QuoteDeliveryTarget != target ||
            account.QuoteDeliveryContactId != request.ContactId)
        {
            account.QuoteDeliveryTarget = target!;
            account.QuoteDeliveryContactId = target == "client" ? request.ContactId : null;
            account.UpdatedAt = DateTime.UtcNow;
            account.UpdatedByUserId = CrmAccess.UserId(http);
            account.RowVersion++;
            var committed = await CrmDataEndpoints.Commit(db, ct);
            if (committed is not Microsoft.AspNetCore.Http.HttpResults.Ok)
                return committed;
        }

        return Results.Ok(new {
            deliveryTarget = account.QuoteDeliveryTarget,
            contactId = account.QuoteDeliveryContactId,
            rowVersion = account.RowVersion
        });
    }
}
