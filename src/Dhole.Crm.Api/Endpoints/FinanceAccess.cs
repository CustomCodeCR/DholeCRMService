using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Dhole.Crm.Api.Endpoints;
internal static class FinanceAccess
{
    public static string? RequestKey(HttpContext h)
    {
        var raw=h.Request.Headers["Idempotency-Key"].ToString();
        if(raw.Length is <8 or >128 || raw.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c is '-' or '_')))
            return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Actor(h)+":"+raw)));
    }
    public static string PayloadHash<T>(T value)=>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    public static Guid Actor(HttpContext h)=>CrmAccess.UserId(h);
    public static bool Has(HttpContext h,string scope)=>CrmAccess.Scope(h,scope);
    public static bool Utc(DateTime dt)=>dt.Kind==DateTimeKind.Utc;
    public static Task<bool> Allowed(Guid accountId,CrmDbContext db,HttpContext h,CancellationToken ct)=>
        CrmAccess.CanSeeAccount(db,h,accountId,ct);
    public static string Snapshot(CreditProfile p)=>JsonSerializer.Serialize(new {
        p.CreditEnabled,p.CreditDays,p.CreditLimit,p.CurrencyCode,p.MonthlyFinancingPct,p.LateInterestMonthlyPct,
        p.PaymentConditions,p.Status,p.EffectiveFrom,p.ReviewAt });
    public static void Audit(CrmDbContext db,CreditProfile p,Guid actor,string before,string reason)
    {
        var history = new CreditTermsHistory
        {
            CreditProfileId = p.Id, ChangedBy = actor, CreatedByUserId = actor,
            OldValuesJson = before, NewValuesJson = Snapshot(p),
            ApprovalReason = reason, EffectiveAt = p.EffectiveFrom
        };
        db.CreditTermsHistory.Add(history);
        // Same ID links the unified event to its authoritative finance audit record.
        // Both rows are committed atomically by the existing SaveChanges transaction.
        db.AccountChangeHistories.Add(new AccountChangeHistory
        {
            Id = history.Id, AccountId = p.AccountId,
            ActorUserId = actor == Guid.Empty ? null : actor,
            CreatedByUserId = actor,
            CreatedAt = history.ChangedAt, UpdatedAt = history.ChangedAt,
            AreaCode = "CREDIT",
            ActionCode = CreditHistoryAction(history.OldValuesJson, history.NewValuesJson, reason),
            ChangesJson = CreditHistoryChanges(history.OldValuesJson, history.NewValuesJson, reason)
        });
    }
    internal static string CreditHistoryAction(string before, string after, string reason)
    {
        using var oldDoc = JsonDocument.Parse(before);
        using var newDoc = JsonDocument.Parse(after);
        var previousStatus = oldDoc.RootElement.TryGetProperty("Status", out var prev) ? prev.GetString() : null;
        var currentStatus = newDoc.RootElement.TryGetProperty("Status", out var current) ? current.GetString() : null;
        if (currentStatus == "Approved" && previousStatus != "Approved") return "CREDIT_APPROVED";
        if (currentStatus == "Rejected") return "CREDIT_REJECTED";
        if (currentStatus == "Superseded") return "CREDIT_SUPERSEDED";
        if (reason.Contains("coverage", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("cobertura", StringComparison.OrdinalIgnoreCase))
            return "CREDIT_COVERAGE_CHANGED";
        if (oldDoc.RootElement.EnumerateObject().Any() == false) return "CREDIT_REQUESTED";
        if (currentStatus == "PendingApproval") return "CREDIT_REVISED";
        return "CREDIT_UPDATED";
    }

    private static string? CreditHistoryValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        _ => value.ToString(),
    };

    internal static string CreditHistoryChanges(string before, string after, string reason)
    {
        using var oldDoc = JsonDocument.Parse(before);
        using var newDoc = JsonDocument.Parse(after);
        var oldFields = oldDoc.RootElement.EnumerateObject()
            .ToDictionary(x => x.Name, x => CreditHistoryValue(x.Value), StringComparer.Ordinal);
        var newFields = newDoc.RootElement.EnumerateObject()
            .ToDictionary(x => x.Name, x => CreditHistoryValue(x.Value), StringComparer.Ordinal);
        var changes = oldFields.Keys.Union(newFields.Keys, StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(key =>
            {
                oldFields.TryGetValue(key, out var previous);
                newFields.TryGetValue(key, out var current);
                return new AccountFieldChange(key, previous, current);
            })
            .Where(x => !string.Equals(x.Before, x.After, StringComparison.Ordinal))
            .ToList();
        if (!string.IsNullOrWhiteSpace(reason))
            changes.Add(new AccountFieldChange("ApprovalReason", null, reason));
        return JsonSerializer.Serialize(changes,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    public static IResult Bad(string message)=>CrmDataEndpoints.Bad(message);
    public static IResult Conflict(string message)=>CrmDataEndpoints.Conflict(message);
    public static Task<IResult> Save(CrmDbContext db,CancellationToken ct)=>CrmDataEndpoints.Commit(db,ct);
}
