using System.Text.Json;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Dhole.Crm.Infrastructure.Realtime;

/// <summary>
/// Fixed collection-only invalidations in the existing durable CRM outbox.
/// The envelope never includes customer data or unrestricted recipient lists.
/// </summary>
public static class CrmCollectionRealtimeCommit
{
    public static readonly Guid AccountListId =
        Guid.Parse("00000000-0000-4000-8000-000000000045");
    public static readonly Guid LeadListId =
        Guid.Parse("00000000-0000-4000-8000-000000000046");

    public static async Task AppendCollectionAsync(CrmDbContext db, string kind,
        long version, string correlationId, CancellationToken ct)
    {
        var id = kind switch
        {
            "accountlist" => AccountListId,
            "leadlist" => LeadListId,
            _ => throw new ArgumentException("Unknown CRM collection", nameof(kind))
        };
        var eventId = Guid.NewGuid();
        var envelope = JsonSerializer.Serialize(new
        {
            eventId,
            eventType = "crm." + kind + ".changed.v1",
            schemaVersion = 1,
            aggregateType = kind,
            aggregateId = id.ToString("D"),
            aggregateVersion = Math.Max(1, version),
            occurredAtUtc = DateTimeOffset.UtcNow,
            producer = "DholeCRMService",
            correlationId = string.IsNullOrWhiteSpace(correlationId)
                ? eventId.ToString("N") : correlationId,
            audienceRef = "crm:" + kind + ":" + id.ToString("D"),
            payload = new { changedFields = new[] { "collection" } }
        });
        await CrmRealtimeOutboxWriter.AppendAsync(db, eventId, envelope, ct);
    }

    public static async Task SaveLeadAsync(CrmDbContext db, IConfiguration flags,
        string correlationId, CancellationToken ct)
    {
        if (!CrmAccountRealtimeCommit.IsEnabled(flags) ||
            !db.ChangeTracker.Entries<Lead>().Any(x =>
                x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        // Join the existing Serializable transaction of lead conversion if one
        // exists. Otherwise own a transaction, keeping the lead and signal atomic.
        var existing = db.Database.CurrentTransaction;
        var transaction = existing is null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            var revision = db.ChangeTracker.Entries<Lead>()
                .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .Select(x => x.Entity.RowVersion)
                .DefaultIfEmpty(1)
                .Max();
            await db.SaveChangesAsync(ct);
            await AppendCollectionAsync(db, "leadlist", revision, correlationId, ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }
}
