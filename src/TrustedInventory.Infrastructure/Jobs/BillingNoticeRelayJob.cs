using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using TrustedInventory.Application.Jobs;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Infrastructure.Jobs;

/// <summary>
/// Moves billing notices into each tenant's outbox (DEC-064). Runs in a per-tenant ti_app transaction, so the control-plane
/// role never needs an outbox privilege (SAS-004); the notice is marked relayed in the same transaction as the outbox row.
/// </summary>
public sealed partial class BillingNoticeRelayJob(TenantJobRunner runner, ILogger<BillingNoticeRelayJob> log) : IJob
{
    private const int Batch = 100;

    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var (tenantId, status) in await runner.TenantsAsync(BackgroundJob.BillingNotices, ct))
        {
            try
            {
                var relayed = await runner.UnitOfWorkFor(tenantId, status).WriteAsync(async (s, c) =>
                {
                    var (conn, tx) = TenantJobRunner.Raw(s);
                    var notices = new List<(Guid Id, string Kind, string Detail)>();
                    await using (var cmd = new NpgsqlCommand("""
                        SELECT id, kind::text, detail::text FROM cp.billing_notice
                        WHERE relayed_at IS NULL ORDER BY created_at, id LIMIT @n FOR UPDATE SKIP LOCKED
                        """, conn, tx))
                    {
                        cmd.Parameters.AddWithValue("n", Batch);
                        await using var r = await cmd.ExecuteReaderAsync(c);
                        while (await r.ReadAsync(c))
                            notices.Add((r.GetGuid(0), r.GetString(1), r.GetString(2)));
                    }
                    foreach (var (id, kind, detail) in notices)
                    {
                        s.Publish("Billing" + kind, new { noticeId = id, kind, detail = JsonNode.Parse(detail) });
                        await using var mark = new NpgsqlCommand(
                            "UPDATE cp.billing_notice SET relayed_at = GREATEST(@now, created_at) WHERE id = @id", conn, tx);
                        mark.Parameters.AddWithValue("now", s.Now);
                        mark.Parameters.AddWithValue("id", id);
                        await mark.ExecuteNonQueryAsync(c);
                    }
                    return notices.Count;
                }, ct);
                if (relayed > 0)
                    LogRelayed(log, relayed, tenantId);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(log, e, tenantId);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Relayed {Count} billing notices to the outbox of tenant {TenantId}")]
    private static partial void LogRelayed(ILogger logger, int count, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Billing notice relay failed for tenant {TenantId}; retried next run")]
    private static partial void LogFailed(ILogger logger, Exception error, Guid tenantId);
}
