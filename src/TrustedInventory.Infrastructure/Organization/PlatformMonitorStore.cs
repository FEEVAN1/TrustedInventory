using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Jobs;
using TrustedInventory.Application.Organization;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Organization;

/// <summary>
/// Platform signals and alerts on the ti_control pool (SOP-003, DEC-074). Every query aggregates across tenants and returns counts,
/// ages or latencies; no tenant name, data or stock leaves here (ti_control has no stock privilege, SAS-004).
/// </summary>
public sealed class PlatformMonitorStore(ControlDataSource control) : IPlatformMonitorStore
{
    public async Task<PlatformHealthDto> MeasureAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = control.Source.CreateCommand("""
            SELECT
              (SELECT count(*) FROM cp.provisioning_job WHERE created_at > @now - interval '24 hours'),
              (SELECT count(*) FROM cp.provisioning_job WHERE created_at > @now - interval '24 hours' AND status = 'Ready'),
              (SELECT count(*) FROM cp.provisioning_job WHERE created_at > @now - interval '24 hours' AND status = 'Failed'),
              (SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM ready_at - created_at))
                 FROM cp.provisioning_job WHERE created_at > @now - interval '24 hours' AND ready_at IS NOT NULL),
              (SELECT percentile_cont(0.95) WITHIN GROUP (ORDER BY extract(epoch FROM ready_at - created_at))
                 FROM cp.provisioning_job WHERE created_at > @now - interval '24 hours' AND ready_at IS NOT NULL),
              (SELECT count(*) FROM cp.subscription WHERE state = 'Trialing' AND trial_end > @now AND trial_end <= @now + interval '3 days'),
              (SELECT count(*) FROM cp.subscription s JOIN ti.tenant t ON t.id = s.tenant_id
                 WHERE s.state = 'Trialing' AND s.trial_end <= @now AND t.lifecycle = 'Ready'),
              (SELECT count(*) FROM cp.webhook_inbox WHERE processing_state IN ('Received', 'Failed')),
              (SELECT extract(epoch FROM @now - min(received_at)) FROM cp.webhook_inbox WHERE processing_state IN ('Received', 'Failed')),
              (SELECT count(*) FROM cp.billing_task WHERE status = 'Open' AND kind = 'EntitlementMismatch'),
              (SELECT count(*) FROM cp.billing_task WHERE status = 'Open'),
              (SELECT count(*) FROM cp.billing_task WHERE status = 'Open' AND created_at < @now - interval '24 hours'),
              (SELECT count(*) FROM cp.invoice WHERE status = 'Open'),
              (SELECT count(*) FROM ti.tenant t
                 LEFT JOIN ti.usage_counter u ON u.tenant_id = t.id AND u.metric = 'active_seats'
                 WHERE t.lifecycle = 'Ready'
                   AND coalesce(u.used, 0) <> (SELECT count(*) FROM ti.membership m WHERE m.tenant_id = t.id AND m.active)),
              (SELECT count(*) FROM cp.platform_alert WHERE status <> 'Closed')
            """);
        cmd.Parameters.AddWithValue("now", now);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        double? D(int i) => r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
        return new PlatformHealthDto(now, r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), D(3), D(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), D(8),
            r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), r.GetInt64(12), r.GetInt64(13), r.GetInt64(14));
    }

    public async Task<int> ApplySignalsAsync(IReadOnlyList<AlertSignal> signals, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var changed = 0;
        foreach (var s in signals)
        {
            var key = s.Kind.ToString();
            if (s.Present)
            {
                await using var upsert = new NpgsqlCommand("""
                    INSERT INTO cp.platform_alert (kind, severity, dedupe_key, detail, opened_at, last_seen_at)
                    VALUES (@kind::cp.platform_alert_kind, @severity, @key, @detail::jsonb, @now, @now)
                    ON CONFLICT (dedupe_key) WHERE status <> 'Closed'
                    DO UPDATE SET detail = EXCLUDED.detail, last_seen_at = EXCLUDED.last_seen_at, severity = EXCLUDED.severity
                    """, conn, tx);
                upsert.Parameters.AddWithValue("kind", key);
                upsert.Parameters.AddWithValue("severity", s.Severity);
                upsert.Parameters.AddWithValue("key", key);
                upsert.Parameters.AddWithValue("detail", JsonSerializer.Serialize(s.Detail, JsonDefaults.Options));
                upsert.Parameters.AddWithValue("now", now);
                changed += await upsert.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using var close = new NpgsqlCommand("""
                    UPDATE cp.platform_alert SET status = 'Closed', closed_at = @now, version = version + 1
                    WHERE dedupe_key = @key AND status <> 'Closed'
                    """, conn, tx);
                close.Parameters.AddWithValue("key", key);
                close.Parameters.AddWithValue("now", now);
                changed += await close.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
        return changed;
    }

    public async Task<IReadOnlyList<PlatformAlertDto>> ListAsync(PlatformAlertStatus? status, CursorPosition? after, int take, CancellationToken ct)
    {
        await using var cmd = control.Source.CreateCommand($"""
            {Select}
            WHERE (@status::text IS NULL OR status::text = @status::text)
              AND (@after_at::timestamptz IS NULL OR opened_at < @after_at OR (opened_at = @after_at AND id < @after_id))
            ORDER BY opened_at DESC, id DESC LIMIT @take
            """);
        cmd.Parameters.AddWithValue("status", (object?)status?.ToString() ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("after_at", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)after?.CreatedAt ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("after_id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)after?.Id ?? DBNull.Value });
        cmd.Parameters.AddWithValue("take", take);
        return await ReadAsync(cmd, ct);
    }

    public async Task<PlatformAlertDto?> AcknowledgeAsync(Guid id, int expectedVersion, string subject, string note, DateTimeOffset now,
        CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT version, status::text FROM cp.platform_alert WHERE id = @id FOR UPDATE", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("id", id);
            await using var r = await lockCmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return null;
            if (r.GetInt32(0) != expectedVersion)
                throw AppException.VersionConflict();
            if (r.GetString(1) != nameof(PlatformAlertStatus.Open))
                throw AppException.InvalidState("Only an open alert can be acknowledged.");
        }
        await using (var update = new NpgsqlCommand("""
            UPDATE cp.platform_alert SET status = 'Acknowledged', acknowledged_by = @by, acknowledged_at = @now, acknowledgement = @note,
                   version = version + 1
            WHERE id = @id
            """, conn, tx))
        {
            update.Parameters.AddWithValue("by", subject);
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("note", note);
            update.Parameters.AddWithValue("id", id);
            await update.ExecuteNonQueryAsync(ct);
        }
        await using var read = new NpgsqlCommand($"{Select} WHERE id = @id", conn, tx);
        read.Parameters.AddWithValue("id", id);
        var dto = (await ReadAsync(read, ct)).Single();
        await tx.CommitAsync(ct);
        return dto;
    }

    private const string Select = """
        SELECT id, kind::text, severity, status::text, detail::text, opened_at, last_seen_at, acknowledged_by, acknowledged_at, acknowledgement,
               closed_at, version
        FROM cp.platform_alert
        """;

    private static async Task<List<PlatformAlertDto>> ReadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<PlatformAlertDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new PlatformAlertDto(r.GetGuid(0), Enum.Parse<PlatformAlertKind>(r.GetString(1)), r.GetString(2),
                Enum.Parse<PlatformAlertStatus>(r.GetString(3)),
                JsonSerializer.Deserialize<Dictionary<string, long>>(r.GetString(4), JsonDefaults.Options) ?? [],
                r.GetFieldValue<DateTimeOffset>(5), r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), r.IsDBNull(9) ? null : r.GetString(9),
                r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10), r.GetInt32(11)));
        return list;
    }
}

/// <summary>Measures platform signals and keeps the alert table in step (SOP-003). Runs every minute in the worker.</summary>
public sealed partial class PlatformMonitorJob(IPlatformMonitorStore store, TimeProvider clock, ILogger<PlatformMonitorJob> log) : IJob
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var health = await store.MeasureAsync(now, ct);
        var signals = PlatformMonitorService.Evaluate(health);
        await store.ApplySignalsAsync(signals, now, ct);
        foreach (var s in signals.Where(s => s.Present))
            LogAlert(log, s.Kind, s.Severity);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Platform alert {Kind} ({Severity}) is active")]
    private static partial void LogAlert(ILogger logger, PlatformAlertKind kind, string severity);
}
