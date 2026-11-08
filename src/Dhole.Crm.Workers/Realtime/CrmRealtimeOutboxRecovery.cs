using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace Dhole.Crm.Workers.Realtime;

/// <summary>
/// An OPERATOR-ONLY recovery primitive. No HTTP endpoint or hosted job calls it.
/// Intended for evidence-backed replay after Redis has lost persisted stream
/// entries. The same event IDs are reused so Inbox dedup prevents side effects.
/// </summary>
public sealed class CrmRealtimeOutboxRecovery(string connectionString)
{
    public async Task<int> RequeuePublishedWindowAsync(
        string stream,
        DateTimeOffset fromUtc,
        DateTimeOffset untilUtc,
        string approvalReference,
        bool explicitlyApproved,
        int maxRows = 1000,
        CancellationToken ct = default)
    {
        if (!explicitlyApproved)
            throw new InvalidOperationException("Human approval required for Redis-loss replay.");
        if (string.IsNullOrWhiteSpace(approvalReference) ||
            approvalReference.Length is < 8 or > 120)
            throw new ArgumentException("Auditable approval reference required.", nameof(approvalReference));
        if (string.IsNullOrWhiteSpace(stream) || stream.Length > 120)
            throw new ArgumentException("Invalid stream name.", nameof(stream));
        if (fromUtc.Offset != TimeSpan.Zero || untilUtc.Offset != TimeSpan.Zero ||
            fromUtc >= untilUtc || untilUtc - fromUtc > TimeSpan.FromDays(30))
            throw new ArgumentOutOfRangeException(nameof(fromUtc), "UTC replay window must be <= 30 days.");
        if (maxRows is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maxRows));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using var cmd = new NpgsqlCommand("""
WITH replay AS (
    SELECT event_id FROM crm.crm_realtime_outbox
    WHERE status = 'Published' AND stream_name = @stream
      AND published_at >= @from_utc AND published_at < @until_utc
    ORDER BY published_at, event_id LIMIT @limit
    FOR UPDATE SKIP LOCKED
)
UPDATE crm.crm_realtime_outbox AS o
SET status = 'Pending', attempt_count = 0, next_attempt_at = now(),
    published_at = NULL, stream_id = NULL, lease_token = NULL, lease_until = NULL,
    last_error = 'approved_redis_loss_replay',
    replay_count = replay_count + 1, last_replay_at = now(),
    replay_approval_id = @approval
FROM replay WHERE o.event_id = replay.event_id;
""", connection, tx);
        cmd.Parameters.Add(new NpgsqlParameter("stream", NpgsqlDbType.Varchar) { Value = stream });
        cmd.Parameters.Add(new NpgsqlParameter("from_utc", NpgsqlDbType.TimestampTz) { Value = fromUtc });
        cmd.Parameters.Add(new NpgsqlParameter("until_utc", NpgsqlDbType.TimestampTz) { Value = untilUtc });
        cmd.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = maxRows });
        cmd.Parameters.Add(new NpgsqlParameter("approval", NpgsqlDbType.Varchar) { Value = approvalReference });
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return affected;
    }
}
