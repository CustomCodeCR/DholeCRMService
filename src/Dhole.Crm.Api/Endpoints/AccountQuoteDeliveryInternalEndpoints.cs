using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

// Private service-to-service route, intentionally not exposed by the /api/crm gateway.
// Never trust caller supplied account/contact email or allow unauthenticated reads.
internal static class AccountQuoteDeliveryInternalEndpoints
{
    internal static WebApplication MapInternalAccountQuoteDelivery(this WebApplication app)
    {
        app.MapGet("/api/internal/crm/accounts/{id:guid}/quote-delivery", Get)
            .AllowAnonymous();
        return app;
    }

    private static async Task<IResult> Get(
        Guid id, CrmDbContext db, HttpContext http,
        IAuthReferenceService auth, IConfiguration configuration, CancellationToken ct)
    {
        var expected = configuration["Auth:Client:InternalServiceKey"]
            ?? configuration["INTERNAL_SERVICE_KEY"];
        var supplied = http.Request.Headers["X-Dhole-Service-Key"].ToString();
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(supplied) ||
            !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
                SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(
            x => x.Id == id && x.ArchivedAt == null, ct);
        if (account is null) return Results.NotFound();

        var assigned = await auth.GetActiveAsync(account.ExecutiveUserId, ct);
        var sellerName = account.IsCastroFallasExecutive
            ? "Castro Fallas" : assigned?.DisplayName;
        var sellerEmail = assigned?.Email;
        if (!ValidEmail(sellerEmail))
            return Results.Conflict(new {
                code = "Crm.AccountSellerEmailMissing",
                message = "La cuenta no tiene un correo válido del vendedor asignado."
            });

        string? contactName = null;
        string? contactEmail = null;
        if (account.QuoteDeliveryTarget == "client")
        {
            if (account.QuoteDeliveryContactId is not { } contactId)
                return Results.Conflict(new {
                    code = "Crm.AccountQuoteContactMissing",
                    message = "La cuenta no tiene contacto destinatario configurado."
                });
            var contact = await (from link in db.AccountContacts.AsNoTracking()
                join person in db.Contacts.AsNoTracking() on link.ContactId equals person.Id
                where link.AccountId == id && link.ContactId == contactId &&
                    link.ActiveTo == null && person.ArchivedAt == null
                select new { person.Name, person.Email }).FirstOrDefaultAsync(ct);
            if (contact is null || !ValidEmail(contact.Email))
                return Results.Conflict(new {
                    code = "Crm.AccountQuoteContactInvalid",
                    message = "El contacto de envío ya no está vinculado o no tiene correo válido."
                });
            contactName = contact.Name;
            contactEmail = contact.Email!.Trim();
        }

        return Results.Ok(new {
            deliveryTarget = account.QuoteDeliveryTarget,
            contactId = account.QuoteDeliveryContactId,
            contactName,
            contactEmail,
            sellerName,
            sellerEmail = sellerEmail!.Trim(),
        });
    }

    private static bool ValidEmail(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        MailAddress.TryCreate(value.Trim(), out var parsed) &&
        string.Equals(parsed.Address, value.Trim(), StringComparison.OrdinalIgnoreCase);
}
