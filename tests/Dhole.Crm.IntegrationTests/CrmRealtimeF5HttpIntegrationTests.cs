using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Dhole.Crm.Api.Endpoints;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.DependencyInjection;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Dhole.Crm.IntegrationTests;

[TestClass]
public sealed class CrmRealtimeF5HttpIntegrationTests
{
    private static string Postgres => Environment.GetEnvironmentVariable("CRM_TEST_POSTGRES")
        ?? throw new InvalidOperationException("CRM_TEST_POSTGRES must be a real CI PostgreSQL connection");

    private sealed class F5AuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-F5-CI-Actor", out var raw) ||
                !Guid.TryParse(raw.ToString(), out var actor))
                return Task.FromResult(AuthenticateResult.Fail("CI actor missing"));
            var claims = new[] { new Claim("user_id", actor.ToString("D")) };
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(
                    new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }

    private sealed class F5AuthReferences(Guid owner, Guid reader, Guid forbidden) : IAuthReferenceService
    {
        public Task<AuthUserReference?> GetActiveAsync(Guid id, CancellationToken ct)
        {
            string[]? scopes =
                id == owner ? ["crm.account.update", "crm.account.read", "crm.account.create", "crm.account.archive", "crm.account.assign"] :
                id == reader ? ["crm.account.read-all"] :
                id == forbidden ? ["crm.account.read"] : null;
            return Task.FromResult(scopes is null
                ? null : new AuthUserReference(id, true, scopes));
        }
    }

    private sealed class F5ConfigReferences : IConfigReferenceService
    {
        public Task<bool> IsActiveCountryAsync(Guid id, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> IsCostaRicaCountryAsync(Guid id, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> IsActiveCodeAsync(string group, string code, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> IsActiveCurrencyIsoAsync(string code, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<ActiveCurrencyOption>> GetActiveCurrencyOptionsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ActiveCurrencyOption>>([]);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid actor, object? body = null)
    {
        var msg = new HttpRequestMessage(method, path);
        msg.Headers.Add("X-F5-CI-Actor", actor.ToString("D"));
        if (body is not null) msg.Content = JsonContent.Create(body);
        return msg;
    }

    [TestMethod]
    public async Task Real_authenticated_account_PATCH_commits_Outbox_reader_gets_snapshot_and_denied_reader_cannot()
    {
        var owner = Guid.NewGuid();
        var reader = Guid.NewGuid();
        var forbidden = Guid.NewGuid();
        var account = new Account
        {
            DisplayName = "Before F5 update",
            LegalName = "F5 REST test legal name",
            ExecutiveUserId = owner,
            CreatedByUserId = owner,
            CategoryType = "CUSTOMER",
            CategoryCode = "D",
            CustomerTypeCode = "cash"
        };
        await using (var seed = new CrmDbContext(
            new DbContextOptionsBuilder<CrmDbContext>().UseNpgsql(Postgres).Options))
        {
            await seed.Database.MigrateAsync();
            seed.Accounts.Add(account);
            await seed.SaveChangesAsync();
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = Postgres,
            ["Features:CrmEnabled"] = "true",
            ["Features:UniversalRealtimeEnabled"] = "true",
            ["Realtime:Outbox:Enabled"] = "true",
            ["Realtime:BatchApproved"] = "true",
            ["Realtime:ReleaseId"] = "rt-20261107-01"
        });
        builder.Services.AddCrmInfrastructure();
        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<IAuthReferenceService>(new F5AuthReferences(owner, reader, forbidden));
        builder.Services.AddSingleton<IConfigReferenceService>(new F5ConfigReferences());
        builder.Services.AddAuthentication("F5-CI")
            .AddScheme<AuthenticationSchemeOptions, F5AuthenticationHandler>("F5-CI", _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCrmDataEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();

        var update = new PatchAccountRequest(1, "After F5 update", account.LegalName,
            "cash", null, null, "CUSTOMER", "D");
        using (var request = Request(HttpMethod.Patch, $"/api/crm/accounts/{account.Id:D}", owner, update))
        using (var response = await client.SendAsync(request))
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

        using (var permittedRequest = Request(HttpMethod.Get, $"/api/crm/accounts/{account.Id:D}", reader))
        using (var permitted = await client.SendAsync(permittedRequest))
        {
            Assert.AreEqual(HttpStatusCode.OK, permitted.StatusCode);
            StringAssert.Contains(await permitted.Content.ReadAsStringAsync(), "After F5 update");
        }
        using (var forbiddenRequest = Request(HttpMethod.Get, $"/api/crm/accounts/{account.Id:D}", forbidden))
        using (var denied = await client.SendAsync(forbiddenRequest))
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);

        // A stale write cannot introduce an extra event or silently overwrite version 2.
        using (var staleRequest = Request(HttpMethod.Patch, $"/api/crm/accounts/{account.Id:D}", owner, update))
        using (var stale = await client.SendAsync(staleRequest))
            Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);

        await using var connection = new NpgsqlConnection(Postgres);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("""
SELECT count(*) FROM crm.crm_realtime_outbox
WHERE (envelope_json->>'aggregateId')::uuid = @id
  AND envelope_json->>'eventType' = 'crm.account.updated.v1'
  AND (envelope_json->>'aggregateVersion')::bigint = 2;
""", connection);
        query.Parameters.AddWithValue("id", account.Id);
        Assert.AreEqual(1L, Convert.ToInt64(await query.ExecuteScalarAsync()));
        await using var read = new NpgsqlCommand(
            "SELECT display_name FROM crm.crm_accounts WHERE id=@id", connection);
        read.Parameters.AddWithValue("id", account.Id);
        Assert.AreEqual("After F5 update", (string?)await read.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task F6_Account_create_activity_logo_archive_and_assign_emit_one_ordered_event_each()
    {
        var owner = Guid.NewGuid();
        var reader = Guid.NewGuid();
        var forbidden = Guid.NewGuid();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = Postgres,
            ["Features:CrmEnabled"] = "true",
            ["Features:UniversalRealtimeEnabled"] = "true",
            ["Realtime:Outbox:Enabled"] = "true",
            ["Realtime:BatchApproved"] = "true",
            ["Realtime:ReleaseId"] = "rt-20261107-01"
        });
        builder.Services.AddCrmInfrastructure();
        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<IAuthReferenceService>(new F5AuthReferences(owner, reader, forbidden));
        builder.Services.AddSingleton<IConfigReferenceService>(new F5ConfigReferences());
        builder.Services.AddAuthentication("F6-CI")
            .AddScheme<AuthenticationSchemeOptions, F5AuthenticationHandler>("F6-CI", _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCrmDataEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        await using (var setup = new CrmDbContext(
            new DbContextOptionsBuilder<CrmDbContext>().UseNpgsql(Postgres).Options))
            await setup.Database.MigrateAsync();

        async Task<Guid> Create(string suffix)
        {
            var data = new CreateAccountRequest(
                "F6 account " + suffix, "F6 legal " + suffix,
                null, null, null, "cash", null, null, "CUSTOMER", "D", owner);
            using var request = Request(HttpMethod.Post, "/api/crm/accounts", owner, data);
            using var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode,
                await response.Content.ReadAsStringAsync());
            using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("id").GetGuid();
        }

        var id = await Create(Guid.NewGuid().ToString("N"));
        using (var activityRequest = Request(HttpMethod.Post,
                $"/api/crm/accounts/{id:D}/business-activities", owner,
                new AddBusinessActivityRequest("LOG", "Realtime test activity", "MANUAL")))
        using (var activityResult = await client.SendAsync(activityRequest))
            Assert.AreEqual(HttpStatusCode.Created, activityResult.StatusCode);

        using (var logoRequest = Request(HttpMethod.Patch,
                $"/api/crm/accounts/{id:D}/logo", owner,
                new SetAccountLogoRequest(2, Guid.NewGuid())))
        using (var logoResult = await client.SendAsync(logoRequest))
            Assert.AreEqual(HttpStatusCode.OK, logoResult.StatusCode);

        using (var archiveRequest = Request(HttpMethod.Post,
                $"/api/crm/accounts/{id:D}/archive", owner,
                new ArchiveAccountRequest(3, "F6 authorized archive")))
        using (var archiveResult = await client.SendAsync(archiveRequest))
            Assert.AreEqual(HttpStatusCode.OK, archiveResult.StatusCode);

        using (var hiddenRequest = Request(HttpMethod.Get, $"/api/crm/accounts/{id:D}", reader))
        using (var hiddenResult = await client.SendAsync(hiddenRequest))
            Assert.AreEqual(HttpStatusCode.NotFound, hiddenResult.StatusCode);

        var assignedId = await Create(Guid.NewGuid().ToString("N"));
        using (var assignRequest = Request(HttpMethod.Post,
                $"/api/crm/accounts/{assignedId:D}/assign", owner,
                new AssignAccountRequest(1, reader, "F6 new owner")))
        using (var assignResult = await client.SendAsync(assignRequest))
            Assert.AreEqual(HttpStatusCode.OK, assignResult.StatusCode);

        await using var connection = new NpgsqlConnection(Postgres);
        await connection.OpenAsync();
        static async Task<string[]> EnvelopeTypes(NpgsqlConnection db, Guid accountId)
        {
            await using var cmd = new NpgsqlCommand("""
SELECT envelope_json->>'eventType'
FROM crm.crm_realtime_outbox
WHERE (envelope_json->>'aggregateId')::uuid = @id
ORDER BY (envelope_json->>'aggregateVersion')::bigint;
""", db);
            cmd.Parameters.AddWithValue("id", accountId);
            var list = new List<string>();
            await using var cursor = await cmd.ExecuteReaderAsync();
            while (await cursor.ReadAsync()) list.Add(cursor.GetString(0));
            return list.ToArray();
        }
        CollectionAssert.AreEqual(new[]
        {
            "crm.account.created.v1",
            "crm.account.activity.added.v1",
            "crm.account.logo.updated.v1",
            "crm.account.archived.v1"
        }, await EnvelopeTypes(connection, id));
        CollectionAssert.AreEqual(new[]
        {
            "crm.account.created.v1",
            "crm.account.assigned.v1"
        }, await EnvelopeTypes(connection, assignedId));
    }

}
