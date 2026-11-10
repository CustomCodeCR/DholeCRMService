using System.Text.Json;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Dhole.Crm.Infrastructure.Realtime;

/// <summary>Commit lead state and metadata-only realtime events atomically.</summary>
public static class CrmLeadRealtimeCommit
{
    public static async Task PersistAsync(CrmDbContext db, Lead lead, IConfiguration flags,
        string correlationId, CancellationToken ct = default, bool accountCreated = false)
    {
        if (!CrmAccountRealtimeCommit.IsEnabled(flags))
        {
            await db.SaveChangesAsync(ct);
            return;
        }
        if (db.Database.CurrentTransaction is not null)
        {
            await SaveWithOutboxAsync(db, lead, correlationId, ct, accountCreated);
            return;
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SaveWithOutboxAsync(db, lead, correlationId, ct, accountCreated);
        await transaction.CommitAsync(ct);
    }

    private static async Task SaveWithOutboxAsync(CrmDbContext db, Lead lead,
        string correlationId, CancellationToken ct, bool accountCreated)
    {
        await db.SaveChangesAsync(ct);
        var eventId = Guid.NewGuid();
        var envelope = JsonSerializer.Serialize(new
        {
            eventId,
            eventType = "crm.lead.changed.v1",
            schemaVersion = 1,
            aggregateType = "lead",
            aggregateId = lead.Id.ToString("D"),
            aggregateVersion = Math.Max(1, lead.RowVersion),
            occurredAtUtc = DateTimeOffset.UtcNow,
            producer = "DholeCRMService",
            correlationId = string.IsNullOrWhiteSpace(correlationId) ? eventId.ToString("D") : correlationId,
            audienceRef = $"crm:lead:{lead.Id:D}",
            payload = new { changedFields = new[] { "lead" } }
        });
        await CrmRealtimeOutboxWriter.AppendAsync(db, eventId, envelope, ct);
        await CrmRealtimeCollectionInvalidation.AppendAsync(
            db, CrmRealtimeCollectionInvalidation.LeadKind, correlationId, ct);
        if (accountCreated)
            await CrmRealtimeCollectionInvalidation.AppendAsync(
                db, CrmRealtimeCollectionInvalidation.AccountKind, correlationId, ct);
    }
}
