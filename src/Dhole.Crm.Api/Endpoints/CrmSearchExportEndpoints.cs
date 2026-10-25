using System.Text;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

/// <summary>
/// Bounded, server-authorized CRM search and report export. Never returns fiscal IDs,
/// contacts, financial terms or arbitrary entity serialization.
/// </summary>
public static class CrmSearchExportEndpoints
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

    public static async Task<IResult> Search(
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

        // SQL ordering precedes DTO projection: EF Core does not translate
        // ordering over constructor-based projections or a cross-table Concat.
        // This keeps stable global pagination without materializing all records.
        var number = Math.Clamp(page ?? 1, 1, 10000);
        var size = Math.Clamp(pageSize ?? 25, 1, 100);
        var offset = (number - 1) * size;
        var items = new List<CrmSearchEntry>(size);
        var accountCount = await accounts.CountAsync(ct);
        var leadCount = await leads.CountAsync(ct);
        var opportunityCount = await opportunities.CountAsync(ct);
        var total = accountCount + leadCount + opportunityCount;

        if (offset < accountCount)
        {
            items.AddRange(await accounts.OrderBy(x => x.DisplayName).ThenBy(x => x.Id)
                .Skip(offset).Take(size).Select(x =>
                    new CrmSearchEntry(x.Id, "account", x.DisplayName)).ToListAsync(ct));
        }
        offset = Math.Max(0, offset - accountCount);
        if (offset < leadCount && items.Count < size)
        {
            items.AddRange(await leads.OrderBy(x => x.Name).ThenBy(x => x.Id)
                .Skip(offset).Take(size - items.Count).Select(x =>
                    new CrmSearchEntry(x.Id, "lead", x.Name)).ToListAsync(ct));
        }
        offset = Math.Max(0, offset - leadCount);
        if (offset < opportunityCount && items.Count < size)
        {
            items.AddRange(await opportunities.OrderBy(x => x.Title).ThenBy(x => x.Id)
                .Skip(offset).Take(size - items.Count).Select(x =>
                    new CrmSearchEntry(x.Id, "opportunity", x.Title)).ToListAsync(ct));
        }
        return Results.Ok(new { items, total, page = number, pageSize = size });
    }

    public static async Task<IResult> ExportAccounts(
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
