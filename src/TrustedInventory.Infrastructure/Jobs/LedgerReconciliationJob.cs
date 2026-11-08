using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using TrustedInventory.Application.Audit;
using TrustedInventory.Application.Jobs;
using TrustedInventory.Domain.Alerts;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Infrastructure.Jobs;

/// <summary>
/// Ledger reconciliation (STK-004, OPS-001, DEC-049). For each tenant the background-work table allows, recomputes every
/// bucket's on hand from the append-only movement ledger and compares it with the stored projection. It never writes
/// a balance. Mismatches open (or refresh) one High ReconciliationMismatch alert and block each mismatched bucket
/// until the alert is closed.
/// </summary>
public sealed partial class LedgerReconciliationJob(TenantJobRunner runner, IAuditSupport support, ILogger<LedgerReconciliationJob> log) : IJob
{
    public const string DedupeKey = "ledger-reconciliation";

    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var (tenantId, status) in await runner.TenantsAsync(BackgroundJob.LedgerReconciliation, ct))
        {
            var mismatches = await runner.UnitOfWorkFor(tenantId, status).WriteAsync((s, c) => ReconcileAsync(s, c), ct);
            if (mismatches > 0)
                LogMismatch(log, mismatches, tenantId);
        }
    }

    private async Task<int> ReconcileAsync(Application.Persistence.ITenantSession s, CancellationToken ct)
    {
        var (conn, tx) = TenantJobRunner.Raw(s);
        var mismatches = new List<(Guid Bucket, decimal Expected, decimal Stored)>();
        await using (var cmd = new NpgsqlCommand("""
            WITH ledger AS (
              SELECT bucket_id, sum(qty) AS expected FROM (
                SELECT destination_bucket_id AS bucket_id, base_qty AS qty FROM ti.stock_movement WHERE destination_bucket_id IS NOT NULL
                UNION ALL
                SELECT source_bucket_id, -base_qty FROM ti.stock_movement WHERE source_bucket_id IS NOT NULL
              ) x GROUP BY bucket_id)
            SELECT b.id, coalesce(l.expected, 0), b.on_hand_qty
            FROM ti.stock_bucket b LEFT JOIN ledger l ON l.bucket_id = b.id
            WHERE coalesce(l.expected, 0) <> b.on_hand_qty
            ORDER BY b.id
            """, conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                mismatches.Add((reader.GetGuid(0), reader.GetDecimal(1), reader.GetDecimal(2)));
        if (mismatches.Count == 0)
            return 0;

        var details = new JsonObject
        {
            ["detectedAt"] = s.Now.ToString("O", CultureInfo.InvariantCulture),
            ["detectedBy"] = TenantJobRunner.Subject,
            ["buckets"] = new JsonArray(mismatches.Select(m => (JsonNode)new JsonObject
            {
                ["bucketId"] = m.Bucket.ToString(),
                ["expectedOnHand"] = m.Expected.ToString(CultureInfo.InvariantCulture),
                ["storedOnHand"] = m.Stored.ToString(CultureInfo.InvariantCulture),
            }).ToArray()),
        };
        if (!await support.RaiseAlertAsync(s, AlertType.ReconciliationMismatch, AlertSeverity.High, DedupeKey, details, ct))
        {
            // Already open: keep its evidence current (the alert row is an operations record, not a balance).
            await using var refresh = new NpgsqlCommand("""
                UPDATE ti.alert SET details = @d, updated_at = @now, version = version + 1
                WHERE dedupe_key = @key AND status <> 'Closed'
                """, conn, tx);
            refresh.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Jsonb) { Value = details.ToJsonString() });
            refresh.Parameters.AddWithValue("now", s.Now);
            refresh.Parameters.AddWithValue("key", DedupeKey);
            await refresh.ExecuteNonQueryAsync(ct);
        }

        foreach (var (bucket, expected, stored) in mismatches)
        {
            await using var block = new NpgsqlCommand("""
                INSERT INTO ti.stock_bucket_block (tenant_id, bucket_id, alert_id, expected_qty, stored_qty, created_at, created_by)
                SELECT a.tenant_id, @bucket, a.id, @expected, @stored, @now, @actor
                FROM ti.alert a WHERE a.dedupe_key = @key AND a.status <> 'Closed'
                ON CONFLICT DO NOTHING
                """, conn, tx);
            block.Parameters.AddWithValue("bucket", bucket);
            block.Parameters.AddWithValue("expected", expected);
            block.Parameters.AddWithValue("stored", stored);
            block.Parameters.AddWithValue("now", s.Now);
            block.Parameters.AddWithValue("actor", TenantJobRunner.Subject);
            block.Parameters.AddWithValue("key", DedupeKey);
            await block.ExecuteNonQueryAsync(ct);
        }
        return mismatches.Count;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Ledger reconciliation found {Count} mismatched buckets for tenant {TenantId}; they are blocked")]
    private static partial void LogMismatch(ILogger logger, int count, Guid tenantId);
}
