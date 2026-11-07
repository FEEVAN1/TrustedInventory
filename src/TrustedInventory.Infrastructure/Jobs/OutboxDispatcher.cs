using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using TrustedInventory.Application.Jobs;

namespace TrustedInventory.Infrastructure.Jobs;

/// <summary>
/// Delivers committed outbox events after commit (ARC-003, NFR-JOBS). Each pass leases a batch per tenant with
/// FOR UPDATE SKIP LOCKED so concurrent workers never share an event, delivers outside the transaction, then
/// records the result. A sent event is never re-sent; failures back off exponentially and, after the maximum
/// attempts, are dead-lettered with a High OutboxDeadLetter alert for operator replay.
/// </summary>
public sealed partial class OutboxDispatcher(
    TenantJobRunner runner, IOutboxSink sink, WorkerOptions options, ILogger<OutboxDispatcher> log) : IJob
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var (tenantId, status) in await runner.TenantsAsync(activeOnly: true, ct))
        {
            var uow = runner.UnitOfWorkFor(tenantId, status);
            var leased = await uow.WriteAsync((s, c) => LeaseAsync(s, tenantId, c), ct);
            foreach (var message in leased)
            {
                try
                {
                    await sink.DeliverAsync(message, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await uow.WriteAsync((s, c) => RecordFailureAsync(s, message, ex, c), ct);
                    continue;
                }
                await uow.WriteAsync((s, c) => MarkSentAsync(s, message, c), ct);
            }
        }
    }

    private async Task<List<OutboxMessage>> LeaseAsync(Application.Persistence.ITenantSession s, Guid tenantId, CancellationToken ct)
    {
        var (conn, tx) = TenantJobRunner.Raw(s);
        await using var cmd = new NpgsqlCommand("""
            WITH due AS (
              SELECT id FROM ti.outbox_event
              WHERE sent_at IS NULL AND dead_lettered_at IS NULL AND next_attempt_at <= @now
                AND (lease_expires_at IS NULL OR lease_expires_at < @now)
              ORDER BY next_attempt_at, id
              LIMIT @batch
              FOR UPDATE SKIP LOCKED)
            UPDATE ti.outbox_event o SET lease_owner = @owner, lease_expires_at = @until
            FROM due WHERE o.id = due.id
            RETURNING o.event_id, o.event_type, o.payload::text, o.attempts
            """, conn, tx);
        cmd.Parameters.AddWithValue("now", s.Now);
        cmd.Parameters.AddWithValue("until", s.Now + options.OutboxLease);
        cmd.Parameters.AddWithValue("batch", options.OutboxBatchSize);
        cmd.Parameters.AddWithValue("owner", options.WorkerId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var messages = new List<OutboxMessage>();
        while (await reader.ReadAsync(ct))
            messages.Add(new OutboxMessage(tenantId, reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) + 1));
        return messages;
    }

    private async Task<int> MarkSentAsync(Application.Persistence.ITenantSession s, OutboxMessage message, CancellationToken ct)
    {
        var (conn, tx) = TenantJobRunner.Raw(s);
        await using var cmd = new NpgsqlCommand("""
            UPDATE ti.outbox_event SET sent_at = @now, lease_owner = NULL, lease_expires_at = NULL
            WHERE event_id = @event AND lease_owner = @owner AND sent_at IS NULL
            """, conn, tx);
        cmd.Parameters.AddWithValue("now", s.Now);
        cmd.Parameters.AddWithValue("event", message.EventId);
        cmd.Parameters.AddWithValue("owner", options.WorkerId);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<int> RecordFailureAsync(Application.Persistence.ITenantSession s, OutboxMessage message, Exception error,
        CancellationToken ct)
    {
        var (conn, tx) = TenantJobRunner.Raw(s);
        var delay = TimeSpan.FromTicks(Math.Min(options.OutboxRetryMax.Ticks,
            options.OutboxRetryBase.Ticks * (1L << Math.Min(message.Attempt - 1, 20))));
        // Only the exception type and a bounded message are kept: no payloads or credentials (section 18).
        var reason = $"{error.GetType().Name}: {error.Message}";
        reason = reason.Length > 500 ? reason[..500] : reason;

        bool deadLettered;
        await using (var cmd = new NpgsqlCommand("""
            UPDATE ti.outbox_event
            SET attempts = attempts + 1, last_error = @error, lease_owner = NULL, lease_expires_at = NULL,
                next_attempt_at = @next,
                dead_lettered_at = CASE WHEN attempts + 1 >= @max THEN @now END
            WHERE event_id = @event AND lease_owner = @owner AND sent_at IS NULL
            RETURNING dead_lettered_at IS NOT NULL
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("error", reason);
            cmd.Parameters.AddWithValue("next", s.Now + delay);
            cmd.Parameters.AddWithValue("max", options.OutboxMaxAttempts);
            cmd.Parameters.AddWithValue("now", s.Now);
            cmd.Parameters.AddWithValue("event", message.EventId);
            cmd.Parameters.AddWithValue("owner", options.WorkerId);
            deadLettered = await cmd.ExecuteScalarAsync(ct) is true;
        }

        if (!deadLettered)
        {
            LogRetry(log, message.EventId, message.Attempt);
            return 0;
        }

        LogDeadLetter(log, message.EventId, message.TenantId);
        await using var alert = new NpgsqlCommand("""
            INSERT INTO ti.alert (tenant_id, type, severity, dedupe_key, details, created_by)
            VALUES (@t, 'OutboxDeadLetter', 'High', @key, @details, @by)
            ON CONFLICT (tenant_id, dedupe_key) WHERE status <> 'Closed' DO NOTHING
            """, conn, tx);
        alert.Parameters.AddWithValue("t", message.TenantId);
        alert.Parameters.AddWithValue("key", $"outbox-dead-letter:{message.EventId}");
        alert.Parameters.Add(new NpgsqlParameter("details", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(new
            {
                eventId = message.EventId,
                eventType = message.EventType,
                attempts = message.Attempt,
                lastError = reason,
            }),
        });
        alert.Parameters.AddWithValue("by", TenantJobRunner.Subject);
        return await alert.ExecuteNonQueryAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox event {EventId} delivery failed (attempt {Attempt}); will retry")]
    private static partial void LogRetry(ILogger logger, Guid eventId, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox event {EventId} for tenant {TenantId} dead-lettered")]
    private static partial void LogDeadLetter(ILogger logger, Guid eventId, Guid tenantId);
}
