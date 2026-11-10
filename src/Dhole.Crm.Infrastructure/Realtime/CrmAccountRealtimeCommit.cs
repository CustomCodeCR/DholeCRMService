using System.Text.Json;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Dhole.Crm.Infrastructure.Realtime;

/// <summary>
/// F5 candidate: the real CRM account write and its minimal invalidation
/// are committed in one PostgreSQL transaction. REST remains the truth.
/// This code is inert until every coordinated release flag is enabled.
/// </summary>
public static class CrmAccountRealtimeCommit
{
    public const string ReleaseId = "rt-20261107-01";

    public static bool IsEnabled(IConfiguration flags) =>
        flags.GetValue<bool>("Features:UniversalRealtimeEnabled") &&
        flags.GetValue<bool>("Realtime:Outbox:Enabled") &&
        flags.GetValue<bool>("Realtime:BatchApproved") &&
        string.Equals(flags["Realtime:ReleaseId"], ReleaseId, StringComparison.Ordinal);

    public static async Task PersistUpdatedAccountAsync(
        CrmDbContext db, Account account, string correlationId, CancellationToken ct = default,
        string eventType = "crm.account.updated.v1")
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(account);
        // Never accept an event name from an HTTP request or arbitrary caller.
        if (eventType is not ("crm.account.updated.v1"
            or "crm.account.created.v1"
            or "crm.account.archived.v1"
            or "crm.account.assigned.v1"
            or "crm.account.logo.updated.v1"
            or "crm.account.activity.added.v1"))
            throw new ArgumentException("Unapproved CRM account event type.", nameof(eventType));
        if (account.Id == Guid.Empty || account.RowVersion < 1)
            throw new ArgumentException("Persisted account id/version required.", nameof(account));

        // EF's audit SaveChanges interceptor and the Outbox participate in the
        // same database commit. Redis failures occur only asynchronously.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);

        var eventId = Guid.NewGuid();
        var envelope = JsonSerializer.Serialize(new
        {
            eventId,
            eventType,
            schemaVersion = 1,
            aggregateType = "account",
            aggregateId = account.Id.ToString("D"),
            aggregateVersion = account.RowVersion,
            occurredAtUtc = DateTimeOffset.UtcNow,
            producer = "DholeCRMService",
            correlationId = string.IsNullOrWhiteSpace(correlationId) ? eventId.ToString("N") : correlationId,
            audienceRef = $"crm:account:{account.Id:D}",
            // Only an invalidation; no customer fields, tokens or PII.
            payload = new { changedFields = new[] { "account" }, aggregateVersion = account.RowVersion }
        });
        await CrmRealtimeOutboxWriter.AppendAsync(db, eventId, envelope, ct);
        // Readers of a collection have not subscribed to the new account GUID.
        await CrmCollectionRealtimeCommit.AppendCollectionAsync(db, "accountlist",
            account.RowVersion, correlationId, ct);
        await tx.CommitAsync(ct);
    }
}
