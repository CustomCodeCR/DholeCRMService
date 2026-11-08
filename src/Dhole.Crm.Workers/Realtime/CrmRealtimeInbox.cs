using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using StackExchange.Redis;

namespace Dhole.Crm.Workers.Realtime;

/// <summary>
/// Runs the inbox insert and the projection effect inside the same database
/// transaction. On failure both roll back; callers ACK Redis only afterward.
/// ConsumerName scopes the deduplication per logical projection, not per host.
/// </summary>
public sealed class CrmRealtimeInboxStore(string connectionString)
{
    public async Task<bool> ApplyOnceAsync(
        Guid eventId,
        string consumerName,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> applyEffect,
        CancellationToken ct = default)
    {
        if (eventId == Guid.Empty) throw new ArgumentException("Event ID required.", nameof(eventId));
        if (string.IsNullOrWhiteSpace(consumerName) || consumerName.Length > 120)
            throw new ArgumentException("Invalid consumer name.", nameof(consumerName));
        ArgumentNullException.ThrowIfNull(applyEffect);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using var command = new NpgsqlCommand("""
INSERT INTO crm.crm_realtime_inbox (event_id, consumer_name)
VALUES (@event_id, @consumer)
ON CONFLICT (event_id, consumer_name) DO NOTHING
RETURNING event_id;
""", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("event_id", NpgsqlDbType.Uuid) { Value = eventId });
        command.Parameters.Add(new NpgsqlParameter("consumer", NpgsqlDbType.Varchar) { Value = consumerName });
        var inserted = await command.ExecuteScalarAsync(ct);
        if (inserted is null)
        {
            // Duplicate: DB is authoritative. Do not invoke the effect again.
            await transaction.CommitAsync(ct);
            return false;
        }
        await applyEffect(connection, transaction, ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}

/// <summary>
/// F2-only consumer harness. F3 bridge will supply its own authorized
/// minimal-invalidation projection. This class is NOT hosted in production.
/// It replays pending entries for a stable consumer id before reading new ones.
/// </summary>
public sealed class CrmRealtimeInboxConsumer(
    IDatabase redis,
    CrmRealtimeInboxStore inbox,
    string stream,
    string group,
    string consumerName)
{
    public async Task<int> ConsumeOnceAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, JsonElement, CancellationToken, Task> projection,
        int count = 100,
        CancellationToken ct = default)
    {
        if (count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(count));
        ArgumentNullException.ThrowIfNull(projection);
        try
        {
            await redis.StreamCreateConsumerGroupAsync(stream, group, "0-0", createStream: true);
        }
        catch (RedisServerException ex) when (
            ex.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase))
        {
            // Group already exists. Never reset last-delivered ID.
        }

        var items = await redis.StreamReadGroupAsync(stream, group, consumerName, "0-0", count);
        if (items.Length == 0)
            items = await redis.StreamReadGroupAsync(stream, group, consumerName, ">", count);

        var acknowledged = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var eventIdRaw = item.Values.FirstOrDefault(v => v.Name == "event_id").Value.ToString();
            var json = item.Values.FirstOrDefault(v => v.Name == "payload_json").Value.ToString();
            if (!Guid.TryParse(eventIdRaw, out var eventId) || eventId == Guid.Empty ||
                string.IsNullOrWhiteSpace(json))
                throw new FormatException("Malformed realtime stream message; leave pending for review.");

            using var doc = JsonDocument.Parse(json);
            await inbox.ApplyOnceAsync(eventId, group, async (connection, tx, token) =>
                await projection(connection, tx, doc.RootElement, token), ct);
            // ACK ONLY after the effect + inbox insert are durably committed.
            await redis.StreamAcknowledgeAsync(stream, group, item.Id);
            acknowledged++;
        }
        return acknowledged;
    }
}
