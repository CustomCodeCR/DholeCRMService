using System.Security.Claims;
using Dhole.Crm.Infrastructure.References;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

internal static class CrmAccess
{
    public static Guid UserId(HttpContext http)
    {
        var raw = http.User.FindFirst("user_id")?.Value
            ?? http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }
    public static bool Scope(HttpContext http, string scope)
    {
        var scopes = http.Items["crm.effective-scopes"] as HashSet<string>;
        return scopes?.Contains(scope) == true;
    }
    // The assigned Auth user of a house account is operational, not an access grant.
    // Never infer permission from the institutional account's ExecutiveUserId.
    public static IQueryable<Account> VisibleAccounts(CrmDbContext db, HttpContext http)
    {
        var actor = UserId(http);
        if (Scope(http, "crm.account.read-all"))
            return db.Accounts.Where(a => a.ArchivedAt == null);
        return db.Accounts.Where(a => a.ArchivedAt == null &&
            ((!a.IsCastroFallasExecutive && a.ExecutiveUserId == actor) ||
             (a.IsCastroFallasExecutive && db.AccountViewerGrants.Any(v =>
                 v.AccountId == a.Id && v.UserId == actor && v.RevokedAt == null))));
    }
    public static Task<bool> CanSeeAccount(CrmDbContext db, HttpContext http, Guid id, CancellationToken ct) =>
        VisibleAccounts(db, http).AnyAsync(a => a.Id == id);
    public static bool AccountAccess(HttpContext http, Guid assignedUserId) =>
        assignedUserId == UserId(http) || Scope(http, "crm.account.read-all");
    public static bool LeadAccess(HttpContext http, Guid assignedUserId) =>
        assignedUserId == UserId(http) || Scope(http, "crm.lead.read-all");
    public static IResult Denied() => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "CRM scope or record access denied");

    public static async ValueTask<object?> Verify(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!http.RequestServices.GetRequiredService<IConfiguration>().GetValue("Features:CrmEnabled", false))
            return Results.Problem(statusCode: 503, title: "CRM is disabled by feature flag");
        var id = UserId(http);
        if (id == Guid.Empty) return Results.Unauthorized();
        try
        {
            var auth = http.RequestServices.GetRequiredService<IAuthReferenceService>();
            var active = await auth.GetActiveAsync(id, http.RequestAborted);
            if (active is null) return Results.Unauthorized();
            http.Items["crm.effective-scopes"] = new HashSet<string>(active.Scopes ?? [],
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception) when (!http.RequestAborted.IsCancellationRequested)
        {
            return Results.Problem(statusCode: 503, title: "Auth dependency unavailable");
        }
        return await next(context);
    }
}
