using System.Text.Json;
using Dhole.Crm.Infrastructure.Persistence;

namespace Dhole.Crm.Infrastructure.Realtime;

/// <summary>Minimum metadata-only invalidations for existing CRM filtered lists.</summary>
public static class CrmRealtimeCollectionInvalidation
{
    public const string AccountKind = "accountlist";
    public const string LeadKind = "leadlist";
    public static readonly Guid AccountListId = Guid.Parse("00000000-0000-4000-8000-000000000045");
    public static readonly Guid LeadListId = Guid.Parse("00000000-0000-4000-8000-000000000046");
    private static long _lastHint;
    public static async Task AppendAsync(CrmDbContext db, string kind, string correlationId,
        CancellationToken ct = default)
    {
        var aggregateId = kind switch
        {
            AccountKind => AccountListId,
            LeadKind => LeadListId,
            _ => throw new ArgumentException("Unsupported collection kind.", nameof(kind))
        };
        long version;
        while (true)
        {
            var now = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10;
            var last = Volatile.Read(ref _lastHint);
            version = Math.Max(now, last + 1);
            if (Interlocked.CompareExchange(ref _lastHint, version, last) == last) break;
        }
        var eventId = Guid.NewGuid();
        var envelope = JsonSerializer.Serialize(new
        {
            eventId,
            eventType = $"crm.{kind}.changed.v1",
            schemaVersion = 1,
            aggregateType = kind,
            aggregateId = aggregateId.ToString("D"),
            aggregateVersion = version,
            occurredAtUtc = DateTimeOffset.UtcNow,
            producer = "DholeCRMService",
            correlationId = string.IsNullOrWhiteSpace(correlationId) ? eventId.ToString("D") : correlationId,
            audienceRef = $"crm:{kind}:{aggregateId:D}",
            payload = new { changedFields = new[] { "collection" } }
        });
        await CrmRealtimeOutboxWriter.AppendAsync(db, eventId, envelope, ct);
    }
}
