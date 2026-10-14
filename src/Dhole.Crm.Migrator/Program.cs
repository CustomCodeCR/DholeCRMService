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
if (!string.Equals(Environment.GetEnvironmentVariable("CRM_SCHEMA_MIGRATION_APPROVED"), "true",
    StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("CRM migration not explicitly approved.");
var options = new DbContextOptionsBuilder<CrmDbContext>().UseNpgsql(pg.ConnectionString).Options;
await using var db = new CrmDbContext(options);
var known = db.Database.GetMigrations().ToArray();
if (known.Length != 2 || known.Any(x => x is not ("20261014193000_InitialCrmData" or "20261014210000_AddCommercialPipelineActivitiesAnalysis")))
    throw new InvalidOperationException("The migrator has an unexpected migration set.");
await db.Database.MigrateAsync();
Console.WriteLine("DholeCRM versioned migrations applied successfully. No data dropped.");
