using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// Dedicated one-shot migration utility. Never auto-migrates on API/Worker startup.
var pg = new NpgsqlConnectionStringBuilder
{
    Host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "postgres",
    Port = int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_PORT"), out var port) ? port : 5432,
    Database = Environment.GetEnvironmentVariable("CRM_DB") ?? "dhole_crm",
    Username = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "",
    Password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "",
    Timeout = 10,
    CommandTimeout = 120,
    Pooling = false
};
if (string.IsNullOrWhiteSpace(pg.Username) || string.IsNullOrWhiteSpace(pg.Password))
    throw new InvalidOperationException("Protected PostgreSQL credentials are missing.");
var options = new DbContextOptionsBuilder<CrmDbContext>().UseNpgsql(pg.ConnectionString).Options;
await using var db = new CrmDbContext(options);
var known = db.Database.GetMigrations().ToArray();
if (known.Length == 0 || !known.Contains("20261014193000_InitialCrmData"))
    throw new InvalidOperationException("CRM initial schema migration is absent from the published image.");
await db.Database.MigrateAsync();
var applied = (await db.Database.GetAppliedMigrationsAsync()).ToHashSet(StringComparer.Ordinal);
if (known.Any(x => !applied.Contains(x)))
    throw new InvalidOperationException("Not all CRM migrations were applied.");
var tableCount = await db.Database.SqlQueryRaw<int>(
    "SELECT COUNT(*)::integer AS \"Value\" FROM information_schema.tables WHERE table_schema = 'crm' AND table_name IN ('crm_accounts','crm_leads','crm_lead_conversions','crm_account_assignments','crm_contacts','crm_account_contacts','crm_addresses','crm_contact_preferences','crm_marketing_consents','crm_account_business_activities')").SingleAsync();
if (tableCount != 10)
    throw new InvalidOperationException($"CRM schema incomplete: expected 10 base tables, found {tableCount}.");
Console.WriteLine($"CRM EF Core migrations applied. Base tables verified: {tableCount}; applied migrations: {applied.Count}.");
