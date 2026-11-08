using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Dhole.Crm.Infrastructure.Realtime;

/// <summary>
/// Writes the realtime envelope with the business write inside the SAME
/// explicit CRM EF transaction. Never write directly to Redis from an endpoint.
/// Existing reminder outbox tables and REST responses remain unchanged.
/// </summary>
public static class CrmRealtimeOutboxWriter
{
    public const string CrmStream = "dhole.crm.realtime.v1";

    private static readonly Regex EventName = new(
        @"^crm\.[a-z][a-z0-9]*(\.[a-z][a-z0-9]*)+\.v[1-9][0-9]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    public static async Task AppendAsync(
        DbContext db,
        Guid eventId,
        string eventEnvelopeJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (eventId == Guid.Empty) throw new ArgumentException("EventId required.", nameof(eventId));
        if (db.Database.CurrentTransaction is not { } transaction)
            throw new InvalidOperationException(
                "Realtime Outbox requires the SAME committed PostgreSQL transaction as its business write.");
        if (string.IsNullOrWhiteSpace(eventEnvelopeJson))
            throw new ArgumentException("Envelope JSON required.", nameof(eventEnvelopeJson));

        // F1 shared envelope is versioned separately until a coordinated
        // package release. These guardrails prevent accidental raw PII payloads
        // before the F6 CRM adapter is linked to the shared contract.
        using var document = JsonDocument.Parse(eventEnvelopeJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("eventId", out var id) ||
            id.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(id.GetString(), out var parsed) || parsed != eventId ||
            !root.TryGetProperty("eventType", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            !EventName.IsMatch(type.GetString() ?? "") ||
            !root.TryGetProperty("aggregateId", out var aggregate) ||
            aggregate.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(aggregate.GetString()) ||
            !root.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid minimal CRM realtime envelope.", nameof(eventEnvelopeJson));
        if (System.Text.Encoding.UTF8.GetByteCount(payload.GetRawText()) > 4096)
            throw new ArgumentException("Realtime payload exceeds 4096 bytes.", nameof(eventEnvelopeJson));

        // This denylist is defense in depth; the shared F1 allowlist must be
        // enforced at the service-owned producer before rollout.
        if (ContainsSensitiveFields(payload))
            throw new ArgumentException("Realtime payload includes forbidden fields.", nameof(eventEnvelopeJson));

        var connection = db.Database.GetDbConnection();
        if (connection is not NpgsqlConnection postgres)
            throw new InvalidOperationException("Realtime pilot requires PostgreSQL.");
        if (postgres.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("Business transaction must have an open connection.");

        await using var command = postgres.CreateCommand();
        command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
        command.CommandText = """
INSERT INTO crm.crm_realtime_outbox
    (event_id, stream_name, envelope_json, status, created_at, next_attempt_at)
VALUES (@id, @stream, @envelope, 'Pending', now(), now())
ON CONFLICT (event_id) DO NOTHING;
""";
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = eventId });
        command.Parameters.Add(new NpgsqlParameter("stream", NpgsqlDbType.Varchar) { Value = CrmStream });
        command.Parameters.Add(new NpgsqlParameter("envelope", NpgsqlDbType.Jsonb) { Value = eventEnvelopeJson });
        await command.ExecuteNonQueryAsync(cancellationToken);
        // Caller commits business change + envelope together. Never commit here.
    }

    private static bool ContainsSensitiveFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Any(ContainsSensitiveFields);
        if (value.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name.Equals("password", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("email", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("token", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("accessToken", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("refreshToken", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("secret", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("phone", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("address", StringComparison.OrdinalIgnoreCase) ||
                ContainsSensitiveFields(property.Value))
                return true;
        }
        return false;
    }
}
