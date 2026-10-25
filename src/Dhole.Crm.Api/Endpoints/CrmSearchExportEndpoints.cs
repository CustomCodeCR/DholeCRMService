using System.Text;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

/// <summary>
/// Bounded, server-authorized CRM search and report export. Never returns fiscal IDs,
/// contacts, financial terms or arbitrary entity serialization.
/// </summary>
internal static class CrmSearchExportEndpoints
{
    public static RouteGroupBuilder MapSearchExport(this RouteGroupBuilder group)
    {
        group.MapGet("/search", Search);
        group.MapGet("/exports/accounts.csv", ExportAccounts);
        return group;
    }

    private static IResult? ValidateSearch(string? q, out string term)
    {
        term = q?.Trim() ?? "";
        if (term.Length is < 2 or > 120)
            return Results.Problem(statusCode: 400, title: "Search requires 2 to 120 characters");
        return null;
    }

    private static async Task<IResult> Search(
        CrmDbContext db, HttpContext http, string? q, int? page, int? pageSize,
        CancellationToken ct)
    {
        var accountRead = CrmDataEndpoints.HasRead(http, "account");
        var leadRead = CrmDataEndpoints.HasRead(http, "lead");
        var opportunityRead = CrmDataEndpoints.HasRead(http, "opportunity");
        if (!accountRead && !leadRead && !opportunityRead) return CrmAccess.Denied();
        var invalid = ValidateSearch(q, out var term);
        if (invalid is not null) return invalid;

        var actor = CrmAccess.UserId(http);
        var accounts = db.Accounts.AsNoTracking()
            .Where(x => accountRead && x.ArchivedAt == null &&
                (x.DisplayName.ToLower().Contains(term.ToLower()) ||
                 x.LegalName.ToLower().Contains(term.ToLower())));
        if (!CrmAccess.Scope(http, "crm.account.read-all"))
            accounts = accounts.Where(x => x.ExecutiveUserId == actor);

        var leads = db.Leads.AsNoTracking()
            .Where(x => leadRead && x.ArchivedAt == null &&
                (x.Name.ToLower().Contains(term.ToLower()) ||
                 (x.CompanyName != null && x.CompanyName.ToLower().Contains(term.ToLower()))));
        if (!CrmAccess.Scope(http, "crm.lead.read-all"))
            leads = leads.Where(x => x.ExecutiveUserId == actor);

        var opportunities = db.Opportunities.AsNoTracking()
            .Where(x => opportunityRead && x.ArchivedAt == null &&
                x.Title.ToLower().Contains(term.ToLower()));
        if (!CrmAccess.Scope(http, "crm.opportunity.read-all"))
            opportunities = opportunities.Where(x => x.ExecutiveUserId == actor);

        IQueryable<CrmSearchEntry> union =
            accounts.Select(x => new CrmSearchEntry(x.Id, "account", x.DisplayName))
                .Concat(leads.Select(x => new CrmSearchEntry(x.Id, "lead", x.Name)))
                .Concat(opportunities.Select(x => new CrmSearchEntry(x.Id, "opportunity", x.Title)));

        var number = Math.Clamp(page ?? 1, 1, 10000);
        var size = Math.Clamp(pageSize ?? 25, 1, 100);
        var total = await union.CountAsync(ct);
        var items = await union.OrderBy(x => x.Kind).ThenBy(x => x.Title).ThenBy(x => x.Id)
            .Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(new { items, total, page = number, pageSize = size });
    }

    private static async Task<IResult> ExportAccounts(
        CrmDbContext db, HttpContext http, ILoggerFactory loggerFactory,
        string? q, int? page, CancellationToken ct)
    {
        // Exports require BOTH reporting authorization and access to the records.
        if (!CrmAccess.Scope(http, "crm.report.read") ||
            !CrmDataEndpoints.HasRead(http, "account"))
            return CrmAccess.Denied();
        if (q is { Length: > 120 })
            return Results.Problem(statusCode: 400, title: "Export filter is too long");

        var actor = CrmAccess.UserId(http);
        var query = db.Accounts.AsNoTracking().Where(x => x.ArchivedAt == null);
        if (!CrmAccess.Scope(http, "crm.account.read-all"))
            query = query.Where(x => x.ExecutiveUserId == actor);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(x => x.DisplayName.ToLower().Contains(term) ||
                                     x.LegalName.ToLower().Contains(term));
        }
        // A single export is capped, so it cannot stream the entire customer portfolio.
        var number = Math.Clamp(page ?? 1, 1, 10000);
        var records = await query.OrderBy(x => x.DisplayName).ThenBy(x => x.Id)
            .Skip((number - 1) * 500).Take(500)
            .Select(x => new CrmAccountExportRow(
                x.Id, x.DisplayName, x.LegalName, x.CountryId, x.CustomerTypeCode,
                x.CategoryType, x.CategoryCode, x.ExecutiveUserId, x.Status))
            .ToListAsync(ct);

        var csv = CrmCsv.WriteAccounts(records);
        loggerFactory.CreateLogger("Dhole.Crm.Exports").LogInformation(
            "CRM accounts export completed actor={ActorId} page={Page} count={Count} request={RequestId}",
            actor, number, records.Count, http.TraceIdentifier);
        return Results.File(Encoding.UTF8.GetBytes("\uFEFF" + csv), "text/csv; charset=utf-8",
            "crm-accounts-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".csv");
    }
}

public sealed record CrmSearchEntry(Guid Id, string Kind, string Title);
public sealed record CrmAccountExportRow(
    Guid Id, string DisplayName, string LegalName, Guid? CountryId,
    string CustomerTypeCode, string CategoryType, string CategoryCode,
    Guid ExecutiveUserId, string Status);

/// <summary>RFC 4180 quoting plus spreadsheet formula neutralization.</summary>
public static class CrmCsv
{
    public static string Escape(string? input)
    {
        var value = input ?? "";
        var first = value.AsSpan().TrimStart();
        if (!first.IsEmpty && "=+-@\t\r\n".Contains(first[0]))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static string WriteAccounts(IEnumerable<CrmAccountExportRow> rows)
    {
        var builder = new StringBuilder();
        builder.Append("Id,DisplayName,LegalName,CountryId,CustomerType,CategoryType,CategoryCode,ExecutiveUserId,Status\r\n");
        foreach (var row in rows)
        {
            builder.Append(string.Join(",", new[] {
                Escape(row.Id.ToString("D")), Escape(row.DisplayName), Escape(row.LegalName),
                Escape(row.CountryId?.ToString("D")), Escape(row.CustomerTypeCode),
                Escape(row.CategoryType), Escape(row.CategoryCode),
                Escape(row.ExecutiveUserId.ToString("D")), Escape(row.Status)
            })).Append("\r\n");
        }
        return builder.ToString();
    }
}
