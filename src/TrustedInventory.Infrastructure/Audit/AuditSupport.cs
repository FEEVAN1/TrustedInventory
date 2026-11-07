using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using TrustedInventory.Application.Audit;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Domain.Alerts;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Audit;

/// <summary>Raw-SQL audit plumbing in the session's own transaction (RLS applies through app.tenant_id).</summary>
public sealed class AuditSupport : IAuditSupport
{
    public async Task<long> StreamHeadAsync(ITenantSession session, CancellationToken ct)
    {
        var s = (TenantUnitOfWork.Session)session;
        await using var cmd = new NpgsqlCommand("SELECT last_sequence FROM ti.audit_stream", s.Connection, s.Transaction);
        return await cmd.ExecuteScalarAsync(ct) is long head ? head : 0;
    }

    public async Task<StoredCheckpoint?> LatestCheckpointAsync(ITenantSession session, long maxSequence, CancellationToken ct)
    {
        var s = (TenantUnitOfWork.Session)session;
        await using var cmd = new NpgsqlCommand("""
            SELECT checkpoint_date, sequence, event_hash, coalesce(external_reference, signing_key_id)
            FROM ti.audit_checkpoint WHERE sequence <= @max ORDER BY sequence DESC, checkpoint_date DESC LIMIT 1
            """, s.Connection, s.Transaction);
        cmd.Parameters.AddWithValue("max", maxSequence);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new StoredCheckpoint(reader.GetFieldValue<DateOnly>(0), reader.GetInt64(1), (byte[])reader[2], reader.GetString(3));
    }

    public async Task<bool> RaiseAlertAsync(ITenantSession session, AlertType type, AlertSeverity severity, string dedupeKey,
        JsonObject details, CancellationToken ct)
    {
        var s = (TenantUnitOfWork.Session)session;
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO ti.alert (tenant_id, type, severity, dedupe_key, details, opened_at, created_at, created_by, updated_at)
            VALUES (@t, @type::ti.alert_type, @severity::ti.alert_severity, @key, @details, @now, @now, @actor, @now)
            ON CONFLICT (tenant_id, dedupe_key) WHERE status <> 'Closed' DO NOTHING
            """, s.Connection, s.Transaction);
        cmd.Parameters.AddWithValue("t", s.Caller.TenantId);
        cmd.Parameters.AddWithValue("type", type.ToString());
        cmd.Parameters.AddWithValue("severity", severity.ToString());
        cmd.Parameters.AddWithValue("key", dedupeKey);
        cmd.Parameters.Add(new NpgsqlParameter("details", NpgsqlDbType.Jsonb) { Value = details.ToJsonString() });
        cmd.Parameters.AddWithValue("now", s.Now);
        cmd.Parameters.AddWithValue("actor", s.Caller.Subject);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
