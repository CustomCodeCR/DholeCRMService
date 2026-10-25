using System.Security.Claims;
using System.Text.Json;
using Dhole.Crm.Api.Endpoints;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.IntegrationTests;

[TestClass]
public sealed class CrmPhase9HardeningTests
{
    [TestMethod]
    public void Csv_escapes_formulas_quotes_newlines_and_preserves_safe_values()
    {
        Assert.AreEqual("\"'=SUM(A1)\"", CrmCsv.Escape("=SUM(A1)"));
        Assert.AreEqual("\"'  @fetch\"", CrmCsv.Escape("  @fetch"));
        Assert.AreEqual("\"'+99\"", CrmCsv.Escape("+99"));
        Assert.AreEqual("\"'-99\"", CrmCsv.Escape("-99"));
        Assert.AreEqual("\"a\"\"b\"", CrmCsv.Escape("a\"b"));
        Assert.AreEqual("\"normal\"", CrmCsv.Escape("normal"));
        Assert.AreEqual("\"line1\nline2\"", CrmCsv.Escape("line1\nline2"));

        var row = new CrmAccountExportRow(
            Guid.NewGuid(), "=IMPORTRANGE()", "Nombre, S.A.",
            Guid.NewGuid(), "cash", "CUSTOMER", "A", Guid.NewGuid(), "Active");
        var csv = CrmCsv.WriteAccounts(new[] { row });
        StringAssert.Contains(csv, "\"'=IMPORTRANGE()\"");
        StringAssert.Contains(csv, "\"Nombre, S.A.\"");
        Assert.IsFalse(csv.Contains("TaxId", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(csv.Contains("CreditLimit", StringComparison.OrdinalIgnoreCase));
    }

    private static DefaultHttpContext Http(Guid user, params string[] scopes)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("user_id", user.ToString("D")) }, "test"))
        };
        context.Items["crm.effective-scopes"] = new HashSet<string>(
            scopes, StringComparer.OrdinalIgnoreCase);
        return context;
    }

    [TestMethod]
    public async Task Search_enforces_owner_and_scope_on_real_PostgreSQL()
    {
        var cs = Environment.GetEnvironmentVariable("CRM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(cs))
        {
            Assert.Inconclusive("Real PostgreSQL integration database required");
            return;
        }

        await using var db = new CrmDbContext(
            new DbContextOptionsBuilder<CrmDbContext>().UseNpgsql(cs).Options);
        await db.Database.MigrateAsync();
        var actor = Guid.NewGuid();
        var other = Guid.NewGuid();
        var term = "F9Security" + Guid.NewGuid().ToString("N")[..12];
        db.Accounts.AddRange(
            new Account {
                DisplayName = term + " own", LegalName = "Test own", ExecutiveUserId = actor,
                CategoryType = "CUSTOMER", CategoryCode = "D", CreatedByUserId = actor
            },
            new Account {
                DisplayName = term + " foreign", LegalName = "Test foreign", ExecutiveUserId = other,
                CategoryType = "CUSTOMER", CategoryCode = "D", CreatedByUserId = other
            });
        await db.SaveChangesAsync();

        var mine = await CrmSearchExportEndpoints.Search(
            db, Http(actor, "crm.account.read"), term, 1, 25, CancellationToken.None);
        using var mineJson = JsonDocument.Parse(JsonSerializer.Serialize(
            ((IValueHttpResult)mine).Value));
        Assert.AreEqual(1, mineJson.RootElement.GetProperty("total").GetInt32());
        Assert.AreEqual(1, mineJson.RootElement.GetProperty("items").GetArrayLength());

        var elevated = await CrmSearchExportEndpoints.Search(
            db, Http(actor, "crm.account.read-all"), term, 1, 25, CancellationToken.None);
        using var elevatedJson = JsonDocument.Parse(JsonSerializer.Serialize(
            ((IValueHttpResult)elevated).Value));
        Assert.AreEqual(2, elevatedJson.RootElement.GetProperty("total").GetInt32());

        var denied = await CrmSearchExportEndpoints.Search(
            db, Http(actor), term, 1, 25, CancellationToken.None);
        Assert.AreEqual(403, ((IStatusCodeHttpResult)denied).StatusCode);
    }

    [TestMethod]
    public async Task Export_requires_report_and_record_scopes()
    {
        var cs = Environment.GetEnvironmentVariable("CRM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(cs))
        {
            Assert.Inconclusive("Real PostgreSQL integration database required");
            return;
        }
        await using var db = new CrmDbContext(
            new DbContextOptionsBuilder<CrmDbContext>().UseNpgsql(cs).Options);
        var context = Http(Guid.NewGuid(), "crm.account.read");
        var result = await CrmSearchExportEndpoints.ExportAccounts(
            db, context, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            null, 1, CancellationToken.None);
        Assert.AreEqual(403, ((IStatusCodeHttpResult)result).StatusCode);
    }
}
