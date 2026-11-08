using System.Text.Json;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.Realtime;
using Dhole.Crm.Workers.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using StackExchange.Redis;

namespace Dhole.Crm.IntegrationTests;

/// <summary>
/// F2 candidate acceptance on REAL PostgreSQL and Redis (CI provides both).
/// An inconclusive or skipped test does not satisfy the F2 gate.
/// </summary>
[TestClass]
public sealed class CrmRealtimeDurabilityPilotIntegrationTests
{
    private static string Postgres => Environment.GetEnvironmentVariable("CRM_TEST_POSTGRES")
        ?? throw new InvalidOperationException("CRM_TEST_POSTGRES required (real PostgreSQL).");

    private static string Redis => Environment.GetEnvironmentVariable("CRM_TEST_REDIS")
        ?? throw new InvalidOperationException("CRM_TEST_REDIS required (real Redis).");

    private static string Envelope(Guid eventId) => JsonSerializer.Serialize(new
    {
        eventId,
        eventType = "crm.account.updated.v1",
        schemaVersion = 1,
        aggregateType = "account",
        aggregateId = eventId.ToString("D"),
        aggregateVersion = 1,
        occurredAtUtc = DateTimeOffset.UtcNow,
        producer = "DholeCRMService",
        correlationId = $"ci-{eventId:N}",
        audienceRef = $"crm:account:{eventId:D}",
        payload = new { changedFields = new[] { "status" }, aggregateVersion = 1 }
    });

    private static async Task<CrmDbContext> OpenAsync()
    {
        var db = new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>().UseNpgsql(Postgres).Options);
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS crm.crm_realtime_pilot_test_effects (
   id uuid PRIMARY KEY,
   applied_count integer NOT NULL DEFAULT 0
);
""");
        return db;
    }

    private static async Task<long> ScalarAsync(string sql, Guid eventId)
    {
        await using var c = new NpgsqlConnection(Postgres);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("id", eventId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task AppendCommittedAsync(CrmDbContext db, Guid eventId)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO crm.crm_realtime_pilot_test_effects(id, applied_count) VALUES ({0}, 0)",
            eventId);
        await CrmRealtimeOutboxWriter.AppendAsync(db, eventId, Envelope(eventId));
        await tx.CommitAsync();
    }

    private static async Task ApplyProjection(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        JsonElement envelope, CancellationToken ct)
    {
        var id = Guid.Parse(envelope.GetProperty("aggregateId").GetString()!);
        await using var cmd = new NpgsqlCommand("""
UPDATE crm.crm_realtime_pilot_test_effects
SET applied_count = applied_count + 1 WHERE id = @id;
""", connection, transaction);
        cmd.Parameters.AddWithValue("id", id);
        Assert.AreEqual(1, await cmd.ExecuteNonQueryAsync(ct));
    }

    private sealed class FailureWriter : ICrmRealtimeStreamWriter
    {
        public Task<string> AddAsync(string stream, Guid eventId, string envelope, CancellationToken ct)
            => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect,
                "simulated Redis outage - no data removed from PostgreSQL");
    }

    [TestMethod]
    public async Task One_thousand_commits_yield_one_thousand_logical_events_and_idempotent_replay()
    {
        await using var db = await OpenAsync();
        using var redis = await ConnectionMultiplexer.ConnectAsync(Redis);
        var database = redis.GetDatabase();
        var ids = new HashSet<Guid>();
        for (var i = 0; i < 1000; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            await AppendCommittedAsync(db, id);
        }

        // Isolate this acceptance's consumer group from unrelated CI tests
        // without changing the production stream configured by the writer.
        var stream = "dhole.crm.realtime.f2ci." + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(Postgres))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
UPDATE crm.crm_realtime_outbox
SET stream_name = @stream WHERE event_id = ANY(@ids);
""", connection);
            command.Parameters.AddWithValue("stream", stream);
            command.Parameters.AddWithValue("ids", ids.ToArray());
            Assert.AreEqual(1000, await command.ExecuteNonQueryAsync());
        }

        var processor = new CrmRealtimeOutboxProcessor(Postgres,
            new CrmRedisRealtimeStreamWriter(database), NullLogger.Instance);
        var published = 0;
        for (var i = 0; i < 20 && published < 1000; i++)
        {
            var batch = await processor.DispatchOnceAsync(100);
            published += batch.Published;
            Assert.AreEqual(0, batch.DeadLettered);
            Assert.AreEqual(0, batch.Retried);
            if (batch.Claimed == 0) break;
        }
        Assert.AreEqual(1000, published, "All 1000 committed events must publish.");

        var group = "crm-f2-ci-" + Guid.NewGuid().ToString("N");
        var inbox = new CrmRealtimeInboxStore(Postgres);
        var consumer = new CrmRealtimeInboxConsumer(database, inbox,
            stream, group, "stable-consumer");
        var acknowledged = 0;
        for (var i = 0; i < 20 && acknowledged < 1000; i++)
        {
            var n = await consumer.ConsumeOnceAsync(ApplyProjection, 100);
            acknowledged += n;
            if (n == 0) break;
        }
        Assert.AreEqual(1000, acknowledged, "All 1000 entries must be ACKed after commit.");

        // Duplicate publishing after PostgreSQL crash-after-XADD is allowed.
        // The same event ID must NOT run the projection twice.
        foreach (var id in ids.Take(100))
            await database.StreamAddAsync(stream, new[]
            {
                new NameValueEntry("event_id", id.ToString("D")),
                new NameValueEntry("event_type", "crm.account.updated.v1"),
                new NameValueEntry("payload_json", Envelope(id))
            });
        var duplicatesAcked = 0;
        for (var i = 0; i < 5 && duplicatesAcked < 100; i++)
            duplicatesAcked += await consumer.ConsumeOnceAsync(ApplyProjection, 100);
        Assert.AreEqual(100, duplicatesAcked);

        await using var connection = new NpgsqlConnection(Postgres);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("""
SELECT count(*) FILTER (WHERE o.status = 'Published') AS published,
       count(*) FILTER (WHERE e.applied_count = 1) AS exactly_once,
       count(*) FILTER (WHERE i.event_id IS NOT NULL) AS inbox
FROM crm.crm_realtime_outbox o
JOIN crm.crm_realtime_pilot_test_effects e ON e.id = o.event_id
LEFT JOIN crm.crm_realtime_inbox i ON i.event_id = o.event_id AND i.consumer_name = @group
WHERE o.event_id = ANY(@ids);
""", connection);
        query.Parameters.AddWithValue("ids", ids.ToArray());
        query.Parameters.AddWithValue("group", group);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(1000L, reader.GetInt64(0));
        Assert.AreEqual(1000L, reader.GetInt64(1));
        Assert.AreEqual(1000L, reader.GetInt64(2));
    }

    [TestMethod]
    public async Task Rolled_back_business_write_never_produces_an_outbox_event()
    {
        await using var db = await OpenAsync();
        var eventId = Guid.NewGuid();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO crm.crm_realtime_pilot_test_effects(id, applied_count) VALUES ({0},0)", eventId);
            await CrmRealtimeOutboxWriter.AppendAsync(db, eventId, Envelope(eventId));
            await tx.RollbackAsync();
        }
        Assert.AreEqual(0L, await ScalarAsync(
            "SELECT count(*) FROM crm.crm_realtime_outbox WHERE event_id=@id", eventId));
        Assert.AreEqual(0L, await ScalarAsync(
            "SELECT count(*) FROM crm.crm_realtime_pilot_test_effects WHERE id=@id", eventId));
    }

    [TestMethod]
    public async Task Writing_without_explicit_business_transaction_is_forbidden()
    {
        await using var db = await OpenAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            CrmRealtimeOutboxWriter.AppendAsync(db, Guid.NewGuid(), Envelope(Guid.NewGuid())));
    }

    [TestMethod]
    public async Task Redis_failure_does_not_undo_committed_business_state_and_can_retry()
    {
        await using var db = await OpenAsync();
        var id = Guid.NewGuid();
        await AppendCommittedAsync(db, id);
        var failing = new CrmRealtimeOutboxProcessor(Postgres, new FailureWriter(), NullLogger.Instance);
        var first = await failing.DispatchOnceAsync(1);
        Assert.AreEqual(1, first.Retried);
        Assert.AreEqual(0, first.Published);
        Assert.AreEqual(1L, await ScalarAsync(
            "SELECT count(*) FROM crm.crm_realtime_pilot_test_effects WHERE id=@id", id));
        Assert.AreEqual(1L, await ScalarAsync(
            "SELECT count(*) FROM crm.crm_realtime_outbox WHERE event_id=@id AND status='Pending'", id));
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE crm.crm_realtime_outbox SET next_attempt_at = now() WHERE event_id = {0}", id);
        using var redis = await ConnectionMultiplexer.ConnectAsync(Redis);
        var retrying = new CrmRealtimeOutboxProcessor(Postgres,
            new CrmRedisRealtimeStreamWriter(redis.GetDatabase()), NullLogger.Instance);
        var result = await retrying.DispatchOnceAsync(1);
        Assert.AreEqual(1, result.Published);
        Assert.AreEqual(1L, await ScalarAsync(
            "SELECT count(*) FROM crm.crm_realtime_outbox WHERE event_id=@id AND status='Published'", id));
    }

    [TestMethod]
    public async Task Failing_delivery_after_five_attempts_is_durable_deadletter()
    {
        await using var db = await OpenAsync();
        var id = Guid.NewGuid();
        await AppendCommittedAsync(db, id);
        var failing = new CrmRealtimeOutboxProcessor(Postgres, new FailureWriter(), NullLogger.Instance);
        for (var i = 0; i < 5; i++)
        {
            var result = await failing.DispatchOnceAsync(1);
            Assert.AreEqual(1, i == 4 ? result.DeadLettered : result.Retried);
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE crm.crm_realtime_outbox SET next_attempt_at = now() WHERE event_id = {0}", id);
        }
        Assert.AreEqual(1L, await ScalarAsync(
            "SELECT count(*) FROM crm.crm_realtime_outbox WHERE event_id=@id AND status='DeadLetter' AND attempt_count=5", id));
    }

    [TestMethod]
    public async Task Redis_pending_entry_replayed_after_commit_before_ack_does_not_repeat_effect()
    {
        await using var db = await OpenAsync();
        using var redis = await ConnectionMultiplexer.ConnectAsync(Redis);
        var cache = redis.GetDatabase();
        var id = Guid.NewGuid();
        await AppendCommittedAsync(db, id);
        // Consume the committed producer outbox so no pending entries pollute
        // the 1000-event acceptance test regardless of MSTest ordering.
        var publisher = new CrmRealtimeOutboxProcessor(Postgres,
            new CrmRedisRealtimeStreamWriter(cache), NullLogger.Instance);
        Assert.AreEqual(1, (await publisher.DispatchOnceAsync(1)).Published);
        var group = "crm-f2-replay-" + Guid.NewGuid().ToString("N");
        var stream = CrmRealtimeOutboxWriter.CrmStream;
        var payload = Envelope(id);
        await cache.StreamAddAsync(stream, new[] {
            new NameValueEntry("event_id", id.ToString("D")),
            new NameValueEntry("event_type", "crm.account.updated.v1"),
            new NameValueEntry("payload_json", payload)
        });
        await cache.StreamCreateConsumerGroupAsync(stream, group, "$", createStream: true);
        // Simulate: effect and Inbox COMMIT succeed, process dies BEFORE XACK.
        await cache.StreamAddAsync(stream, new[] {
            new NameValueEntry("event_id", id.ToString("D")),
            new NameValueEntry("event_type", "crm.account.updated.v1"),
            new NameValueEntry("payload_json", payload)
        });
        var delivery = await cache.StreamReadGroupAsync(stream, group, "stable-consumer", ">", 1);
        Assert.AreEqual(1, delivery.Length);
        var inbox = new CrmRealtimeInboxStore(Postgres);
        Assert.IsTrue(await inbox.ApplyOnceAsync(id, group,
            async (connection, tx, ct) => {
                using var doc = JsonDocument.Parse(payload);
                await ApplyProjection(connection, tx, doc.RootElement, ct);
            }));
        // No XACK. Stable consumer must read its own pending and dedupe.
        var consumer = new CrmRealtimeInboxConsumer(cache, inbox, stream, group, "stable-consumer");
        Assert.AreEqual(1, await consumer.ConsumeOnceAsync(ApplyProjection, 100));
        Assert.AreEqual(1L, await ScalarAsync(
            "SELECT applied_count FROM crm.crm_realtime_pilot_test_effects WHERE id=@id", id));
    }
}
