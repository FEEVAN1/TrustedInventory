using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using TrustedInventory.Application.Audit;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;

namespace TrustedInventory.Infrastructure.Audit;

/// <summary>Who performed the audited change and in which request (AUD-001).</summary>
public sealed record AuditActor(string Subject, ActorType Type, string? RequestId, string? CorrelationId);

/// <summary>
/// Appends audit events to a tenant's hash chain inside the caller's transaction (AUD-002, ARC-003).
/// Locks the tenant's audit_stream row, which serialises sequence assignment; callers invoke this last,
/// after every other lock, per the global lock order (docs/migration-design.md section 5).
/// </summary>
public static class AuditAppender
{
    // Snapshots are hashed as canonical JSON, which allows only integers: decimals are written as strings.
    private static readonly JsonSerializerOptions SnapshotOptions = CreateSnapshotOptions();

    public static JsonNode? Snapshot(object? value) =>
        value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), SnapshotOptions);

    public static async Task AppendAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, AuditActor actor,
        DateTimeOffset occurredAt, IReadOnlyList<AuditEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0)
            return;

        long sequence;
        byte[] previous;
        await using (var head = new NpgsqlCommand(
            "SELECT last_sequence, last_hash FROM ti.audit_stream WHERE tenant_id = @t FOR UPDATE", conn, tx))
        {
            head.Parameters.AddWithValue("t", tenantId);
            await using var reader = await head.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("The tenant has no audit stream; it was not provisioned correctly.");
            sequence = reader.GetInt64(0);
            previous = (byte[])reader[1];
        }

        var at = AuditRecord.Normalize(occurredAt);
        foreach (var entry in entries)
        {
            sequence++;
            var record = new AuditRecord(tenantId, sequence, at, actor.Subject, actor.Type.ToString(), actor.RequestId,
                actor.CorrelationId, entry.EventType, entry.ObjectType, entry.ObjectId, entry.ObjectVersion, entry.Reason,
                Snapshot(entry.Before), Snapshot(entry.After));
            var hash = record.Hash(previous);

            await using var insert = new NpgsqlCommand("""
                INSERT INTO ti.audit_event (tenant_id, sequence, occurred_at, actor_subject, actor_type, request_id,
                  correlation_id, event_type, object_type, object_id, object_version, reason, before_state, after_state,
                  previous_hash, event_hash)
                VALUES (@tenant, @seq, @at, @actor, @actor_type::ti.actor_type, @request, @correlation, @event, @object_type,
                  @object_id, @object_version, @reason, @before, @after, @previous, @hash)
                """, conn, tx);
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("seq", sequence);
            insert.Parameters.AddWithValue("at", at);
            insert.Parameters.AddWithValue("actor", actor.Subject);
            insert.Parameters.AddWithValue("actor_type", actor.Type.ToString());
            insert.Parameters.AddWithValue("request", (object?)actor.RequestId ?? DBNull.Value);
            insert.Parameters.AddWithValue("correlation", (object?)actor.CorrelationId ?? DBNull.Value);
            insert.Parameters.AddWithValue("event", entry.EventType);
            insert.Parameters.AddWithValue("object_type", entry.ObjectType);
            insert.Parameters.AddWithValue("object_id", (object?)entry.ObjectId ?? DBNull.Value);
            insert.Parameters.AddWithValue("object_version", (object?)entry.ObjectVersion ?? DBNull.Value);
            insert.Parameters.AddWithValue("reason", (object?)entry.Reason ?? DBNull.Value);
            insert.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.Jsonb) { Value = (object?)record.Before?.ToJsonString() ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Jsonb) { Value = (object?)record.After?.ToJsonString() ?? DBNull.Value });
            insert.Parameters.AddWithValue("previous", previous);
            insert.Parameters.AddWithValue("hash", hash);
            await insert.ExecuteNonQueryAsync(ct);
            previous = hash;
        }

        await using var update = new NpgsqlCommand(
            "UPDATE ti.audit_stream SET last_sequence = @seq, last_hash = @hash, updated_at = now() WHERE tenant_id = @t", conn, tx);
        update.Parameters.AddWithValue("seq", sequence);
        update.Parameters.AddWithValue("hash", previous);
        update.Parameters.AddWithValue("t", tenantId);
        await update.ExecuteNonQueryAsync(ct);
    }

    private static JsonSerializerOptions CreateSnapshotOptions()
    {
        var options = JsonDefaults.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        options.Converters.Add(new DecimalAsStringConverter());
        options.Converters.Add(new UtcTimestampConverter());
        return options;
    }

    private sealed class DecimalAsStringConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Quantity.Format(value));
    }

    /// <summary>Timestamps in snapshots use one fixed UTC form so re-hashing a stored row is deterministic.</summary>
    private sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture));
    }
}
