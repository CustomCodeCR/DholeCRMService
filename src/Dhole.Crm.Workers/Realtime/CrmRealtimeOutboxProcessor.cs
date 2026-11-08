using System.Data;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using StackExchange.Redis;

namespace Dhole.Crm.Workers.Realtime;

/// <summary>
/// F2 pilot transport boundary; injectable for fault tests. Publishes only
/// committed outbox rows, never business commands or raw entity snapshots.
/// </summary>
public interface ICrmRealtimeStreamWriter
{
    Task<string> AddAsync(string stream, Guid eventId, string envelopeJson, CancellationToken ct);
}

public sealed class CrmRedisRealtimeStreamWriter(IDatabase redis) : ICrmRealtimeStreamWriter
{
    public async Task<string> AddAsync(string stream, Guid eventId, string envelopeJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var doc = JsonDocument.Parse(envelopeJson);
        var eventType = doc.RootElement.GetProperty("eventType").GetString()
            ?? throw new FormatException("Missing event type.");
        // Identity is stable across retries. A crash after XADD but before the
        // PostgreSQL mark can yield duplicate stream entries by design.
        var streamId = await redis.StreamAddAsync(stream, new[]
        {
            new NameValueEntry("event_id", eventId.ToString("D")),
            new NameValueEntry("event_type", eventType),
            new NameValueEntry("payload_json", envelopeJson),
        });
        return streamId.ToString();
    }
}

public sealed record CrmRealtimeOutboxBatchResult(int Claimed, int Published, int Retried, int DeadLettered);

public sealed class CrmRealtimeOutboxProcessor(
    string connectionString,
    ICrmRealtimeStreamWriter streamWriter,
    ILogger logger)
{
    private static readonly Meter Meter = new("Dhole.Crm.Realtime.Outbox", "0.1.0");
    private static readonly Counter<long> PublishedMetric = Meter.CreateCounter<long>("realtime_outbox_published");
    private static readonly Counter<long> RetriedMetric = Meter.CreateCounter<long>("realtime_outbox_retried");
    private static readonly Counter<long> DeadLetterMetric = Meter.CreateCounter<long>("realtime_outbox_deadletter");
    private const int MaxAttempts = 5;

    private sealed record Claimed(Guid EventId, string StreamName, string Json, int Attempts);
    private sealed record Completion(bool IsDeadLetter);

    public static TimeSpan RetryDelay(int attempts, double jitter)
    {
        if (attempts < 1) throw new ArgumentOutOfRangeException(nameof(attempts));
        if (jitter < 0 || jitter > 1 || double.IsNaN(jitter))
            throw new ArgumentOutOfRangeException(nameof(jitter));
        return TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(attempts, 8)) + (jitter * 2)));
    }

    public async Task<CrmRealtimeOutboxBatchResult> DispatchOnceAsync(
        int batchSize = 50,
        CancellationToken ct = default)
    {
        if (batchSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var token = Guid.NewGuid();
        var rows = await ClaimAsync(token, batchSize, ct);
        var published = 0;
        var retried = 0;
        var deadLettered = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var streamId = await streamWriter.AddAsync(row.StreamName, row.EventId, row.Json, ct);
                if (await MarkPublishedAsync(row.EventId, token, streamId, ct))
                {
                    published++;
                    PublishedMetric.Add(1);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // lease expires; durable replay on restart
            }
            catch (Exception ex)
            {
                // Do not log payload, raw connection strings or credentials.
                var delay = RetryDelay(row.Attempts, Random.Shared.NextDouble());
                var result = await MarkFailedAsync(row.EventId, token, row.Attempts,
                    ex.GetType().Name, delay, ct);
                if (result is null) continue; // another worker acquired the expired lease
                if (result.IsDeadLetter)
                {
                    deadLettered++;
                    DeadLetterMetric.Add(1);
                    logger.LogError("CRM realtime event {EventId} placed in durable DeadLetter after {Attempts} attempts",
                        row.EventId, row.Attempts);
                }
                else
                {
                    retried++;
                    RetriedMetric.Add(1);
                    logger.LogWarning("CRM realtime event {EventId} queued for retry attempt {Attempts}",
                        row.EventId, row.Attempts);
                }
            }
        }
        return new(rows.Count, published, retried, deadLettered);
    }

    private async Task<List<Claimed>> ClaimAsync(Guid leaseToken, int batchSize, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using var cmd = new NpgsqlCommand("""
WITH ready AS (
    SELECT event_id FROM crm.crm_realtime_outbox
    WHERE (status = 'Pending' AND next_attempt_at <= now())
       OR (status = 'Leased' AND lease_until < now())
    ORDER BY created_at, event_id
    LIMIT @limit
    FOR UPDATE SKIP LOCKED
)
UPDATE crm.crm_realtime_outbox AS o
SET status = 'Leased',
    attempt_count = o.attempt_count + 1,
    lease_token = @lease,
    lease_until = now() + interval '60 seconds'
FROM ready
WHERE o.event_id = ready.event_id
RETURNING o.event_id, o.stream_name, o.envelope_json::text, o.attempt_count;
""", connection, tx);
        cmd.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = batchSize });
        cmd.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.Uuid) { Value = leaseToken });
        var rows = new List<Claimed>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        await tx.CommitAsync(ct);
        return rows;
    }

    private async Task<bool> MarkPublishedAsync(
        Guid eventId, Guid token, string streamId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
UPDATE crm.crm_realtime_outbox
SET status = 'Published', published_at = now(), stream_id = @stream_id,
    lease_token = NULL, lease_until = NULL, last_error = NULL
WHERE event_id = @event_id AND status = 'Leased' AND lease_token = @lease;
""", connection);
        cmd.Parameters.AddWithValue("stream_id", streamId);
        cmd.Parameters.AddWithValue("event_id", eventId);
        cmd.Parameters.AddWithValue("lease", token);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private async Task<Completion?> MarkFailedAsync(
        Guid eventId, Guid token, int attempt, string failureKind, TimeSpan delay,
        CancellationToken ct)
    {
        var deadLetter = attempt >= MaxAttempts;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
UPDATE crm.crm_realtime_outbox
SET status = @status, next_attempt_at = now() + (@delay_seconds * interval '1 second'),
    last_error = @failure_kind, lease_token = NULL, lease_until = NULL
WHERE event_id = @event_id AND status = 'Leased' AND lease_token = @lease;
""", connection);
        cmd.Parameters.AddWithValue("status", deadLetter ? "DeadLetter" : "Pending");
        cmd.Parameters.AddWithValue("delay_seconds", delay.TotalSeconds);
        cmd.Parameters.AddWithValue("failure_kind", failureKind);
        cmd.Parameters.AddWithValue("event_id", eventId);
        cmd.Parameters.AddWithValue("lease", token);
        return await cmd.ExecuteNonQueryAsync(ct) == 1 ? new Completion(deadLetter) : null;
    }
}
