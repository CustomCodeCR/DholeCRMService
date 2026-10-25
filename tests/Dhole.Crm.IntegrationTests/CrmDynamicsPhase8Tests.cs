using System.Net;
using System.Text;
using System.Text.Json;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Integrations;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Dhole.Crm.IntegrationTests;

[TestClass]
public sealed class CrmDynamicsPhase8Tests
{
    private static IConfiguration Config(Dictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Crm:Dynamics:BaseUrl"] = "https://connector.example.com/",
            ["Crm:Dynamics:IntegrationKey"] = "test-only-not-a-real-secret",
            ["Crm:Dynamics:ConnectionCode"] = "approved",
            ["Crm:Dynamics:SavedQuery"] = "crm_accounts_v1",
            ["Crm:Dynamics:Fields:ExternalId"] = "accountid",
            ["Crm:Dynamics:Fields:DisplayName"] = "name",
            ["Crm:Dynamics:Fields:LegalName"] = "legalname",
            ["Crm:Dynamics:Fields:CountryId"] = "countrycode",
            ["Crm:Dynamics:Fields:TaxId"] = "identification",
            ["Crm:Dynamics:Fields:TaxIdTypeCode"] = "identificationtype",
            ["Crm:Dynamics:Fields:ExecutiveId"] = "sellerid",
            ["Crm:Dynamics:Fields:CustomerType"] = "customertype",
            ["Crm:Dynamics:Fields:CategoryType"] = "categorytype",
            ["Crm:Dynamics:Fields:CategoryCode"] = "categorycode",
            ["Crm:Dynamics:ExecutiveMap:seller-001"] = "33333333-3333-4333-8333-333333333333",
            ["Crm:Dynamics:CountryMap:CR"] = "22222222-2222-4222-8222-222222222222",
            ["Crm:Dynamics:TaxTypeMap:01"] = "CEDULA",
            ["Crm:Dynamics:CodeMap:CustomerType:Cash"] = "cash",
            ["Crm:Dynamics:CodeMap:CategoryType:Customer"] = "CUSTOMER",
            ["Crm:Dynamics:CodeMap:CategoryCode:A"] = "A"
        };
        if (overrides is not null)
            foreach (var (key, value) in overrides) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static JsonDocument Row(string? tax = "0012345") =>
        JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["accountid"] = Guid.NewGuid().ToString("D"),
            ["name"] = "Cuenta de prueba", ["legalname"] = "Razón social de prueba",
            ["countrycode"] = "CR", ["identification"] = tax,
            ["identificationtype"] = "01", ["sellerid"] = "seller-001",
            ["customertype"] = "Cash", ["categorytype"] = "Customer",
            ["categorycode"] = "A"
        }));

    [TestMethod]
    public void Mapper_requires_real_metadata_and_explicit_auth_config_relationships()
    {
        var config = Config();
        var mapper = new CrmDynamicsAccountMapper(config);
        Assert.IsTrue(mapper.Configured);
        using var doc = Row();
        var (candidate, code) = mapper.Parse(doc.RootElement);
        Assert.IsNull(code);
        Assert.IsNotNull(candidate);
        Assert.AreEqual("CEDULA", candidate.TaxIdTypeCode);
        Assert.AreEqual("cash", candidate.CustomerType);
        Assert.AreEqual("CUSTOMER", candidate.CategoryType);
        Assert.AreEqual("A", candidate.CategoryCode);
        Assert.AreEqual(Guid.Parse("33333333-3333-4333-8333-333333333333"), candidate.ExecutiveId);
        var before = mapper.ConfigurationFingerprint();
        var invalid = new CrmDynamicsAccountMapper(Config(new Dictionary<string, string?>
        { ["Crm:Dynamics:CountryMap:CR"] = null }));
        Assert.AreEqual("country_not_mapped", invalid.Parse(doc.RootElement).Error);
        Assert.AreNotEqual(before, invalid.ConfigurationFingerprint(),
            "A job must stop if business mappings changed.");
    }

    [TestMethod]
    public void Mapper_never_infers_marketing_consent_or_unmapped_categories()
    {
        using var doc = Row();
        var mapper = new CrmDynamicsAccountMapper(Config(new Dictionary<string, string?>
        { ["Crm:Dynamics:CodeMap:CustomerType:Cash"] = null }));
        Assert.AreEqual("categories_not_mapped", mapper.Parse(doc.RootElement).Error);
        Assert.IsFalse(typeof(CrmDynamicsAccountCandidate).GetProperties()
            .Any(x => x.Name.Contains("Consent", StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("https://connector.example.com/api/v1/keyed/saved-queries/crm_accounts_v1/execute",
                request.RequestUri?.ToString());
            Assert.IsTrue(request.Headers.Contains("X-Dhole-Api-Key"));
            var body = await request.Content!.ReadAsStringAsync(ct);
            StringAssert.Contains(body, "\"connectionCode\":\"approved\"");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"entity\":\"account\",\"items\":[{\"accountid\":\"11111111-1111-4111-8111-111111111111\"}],\"nextCursor\":null,\"metadataVersion\":6}",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    [TestMethod]
    public async Task Connector_executes_only_approved_bounded_query()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var reader = new CrmDynamicsSavedQueryClient(http, Config());
        var result = await reader.ReadAsync(null, CancellationToken.None);
        Assert.HasCount(1, result.Items);
        Assert.IsNull(result.NextCursor);
        Assert.AreEqual(6, result.MetadataVersion);
        Assert.AreEqual(1, handler.Calls);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await new CrmDynamicsSavedQueryClient(http,
                Config(new Dictionary<string, string?> { ["Crm:Dynamics:BaseUrl"] = "http://localhost:3000/" }))
                .ReadAsync(null, CancellationToken.None));
        Assert.AreEqual(1, handler.Calls, "No unsafe HTTP request may be sent.");
    }

    [TestMethod]
    public async Task Real_postgresql_migration_enforces_external_correlations_and_idempotency()
    {
        var cs = Environment.GetEnvironmentVariable("CRM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(cs)) Assert.Inconclusive("Real PostgreSQL required");
        await using var db = new CrmDbContext(
            new DbContextOptionsBuilder<CrmDbContext>().UseNpgsql(cs).Options);
        await db.Database.MigrateAsync();
        var tables = await db.Database.SqlQueryRaw<string>(
            "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema='crm' AND table_name LIKE 'crm_%sync_%' OR table_schema='crm' AND table_name='crm_external_mappings'")
            .ToListAsync();
        foreach (var name in new[] { "crm_external_mappings", "crm_sync_jobs", "crm_sync_conflicts" })
            CollectionAssert.Contains(tables, name);
        var actor = Guid.NewGuid();
        var job = new CrmSyncJob { CreatedByUserId = actor, RequestKeyHash = Guid.NewGuid().ToString("N") };
        db.SyncJobs.Add(job);
        await db.SaveChangesAsync();
        db.SyncJobs.Add(new CrmSyncJob
        { CreatedByUserId = actor, RequestKeyHash = job.RequestKeyHash });
        await Assert.ThrowsExactlyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
    }
}
