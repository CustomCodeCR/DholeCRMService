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
        db.Accounts.AsNoTracking().AnyAsync(x=>x.Id==accountId&&x.ArchivedAt==null&&
            (x.ExecutiveUserId==Actor(h)||Has(h,"crm.account.read-all")),ct);
    public static string Snapshot(CreditProfile p)=>JsonSerializer.Serialize(new {
        p.CreditEnabled,p.CreditDays,p.CreditLimit,p.CurrencyCode,p.MonthlyFinancingPct,
        p.PaymentConditions,p.Status,p.EffectiveFrom,p.ReviewAt });
    public static void Audit(CrmDbContext db,CreditProfile p,Guid actor,string before,string reason)
    {
        db.CreditTermsHistory.Add(new CreditTermsHistory{CreditProfileId=p.Id,ChangedBy=actor,
            CreatedByUserId=actor,OldValuesJson=before,NewValuesJson=Snapshot(p),
            ApprovalReason=reason,EffectiveAt=p.EffectiveFrom});
    }
    public static IResult Bad(string message)=>CrmDataEndpoints.Bad(message);
    public static IResult Conflict(string message)=>CrmDataEndpoints.Conflict(message);
    public static Task<IResult> Save(CrmDbContext db,CancellationToken ct)=>CrmDataEndpoints.Commit(db,ct);
}
