using Microsoft.Extensions.Logging;
using Npgsql;
using TrustedInventory.Application.Jobs;

namespace TrustedInventory.Infrastructure.Jobs;

/// <summary>
/// Deletes idempotency records past their expiry (STK-007: kept at least seven days by a schema CHECK). This is
/// the only DELETE ti_app holds. Duplicate posting after expiry is still prevented by permanent operation keys.
/// </summary>
public sealed partial class IdempotencyCleanupJob(TenantJobRunner runner, ILogger<IdempotencyCleanupJob> log) : IJob
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var (tenantId, status) in await runner.TenantsAsync(activeOnly: false, ct))
        {
            var deleted = await runner.UnitOfWorkFor(tenantId, status).WriteAsync(async (s, c) =>
            {
                var (conn, tx) = TenantJobRunner.Raw(s);
                await using var cmd = new NpgsqlCommand("DELETE FROM ti.idempotency_record WHERE expires_at < @now", conn, tx);
                cmd.Parameters.AddWithValue("now", s.Now);
                return await cmd.ExecuteNonQueryAsync(c);
            }, ct);
            if (deleted > 0)
                LogDeleted(log, deleted, tenantId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} expired idempotency records for tenant {TenantId}")]
    private static partial void LogDeleted(ILogger logger, int count, Guid tenantId);
}
