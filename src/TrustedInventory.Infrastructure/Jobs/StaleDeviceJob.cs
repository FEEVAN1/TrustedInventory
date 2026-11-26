using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TrustedInventory.Application.Audit;
using TrustedInventory.Application.Inbound;
using TrustedInventory.Application.Jobs;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Domain.Alerts;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Infrastructure.Jobs;

/// <summary>
/// Stale device detection (TEL-002, TEL-005, DEC-077): one StaleDevice alert per placed, active device that has not reported within
/// 300 seconds, closed again when it reports. Blocking itself never waits for this job: allocation, picking and dispatch compute
/// staleness on each command.
/// </summary>
public sealed partial class StaleDeviceJob(TenantJobRunner runner, IAuditSupport support, ILogger<StaleDeviceJob> log) : IJob
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var (tenantId, status) in await runner.TenantsAsync(BackgroundJob.TelemetryEvaluation, ct))
        {
            try
            {
                var raised = await runner.UnitOfWorkFor(tenantId, status).WriteAsync((s, c) => EvaluateAsync(s, support, c), ct);
                if (raised > 0)
                    LogRaised(log, raised, tenantId);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(log, e, tenantId);
            }
        }
    }

    internal static async Task<int> EvaluateAsync(ITenantSession s, IAuditSupport support, CancellationToken ct)
    {
        var cutoff = s.Now - TelemetryRules.StaleAfter;
        var devices = await s.Db.Database.SqlQuery<DeviceRow>($"""
            SELECT d.id AS "Id", d.name AS "Name", a.zone_id AS "ZoneId", z.warehouse_id AS "WarehouseId", d.last_reading_at AS "LastReadingAt",
                   (d.active AND coalesce(d.last_reading_at, d.created_at) <= {cutoff}) AS "Stale"
            FROM ti.device d
            JOIN ti.device_zone_assignment a ON a.device_id = d.id AND a.effective_from <= {s.Now} AND (a.effective_to IS NULL OR a.effective_to > {s.Now})
            JOIN ti.zone z ON z.tenant_id = a.tenant_id AND z.id = a.zone_id
            """).ToListAsync(ct);
        var raised = 0;
        foreach (var d in devices)
        {
            var key = $"stale-device:{d.Id:D}";
            if (d.Stale)
            {
                var details = new JsonObject
                {
                    ["title"] = $"Temperature device {d.Name} has stopped reporting",
                    ["detail"] = d.LastReadingAt is { } last ? $"Last reading at {last:O}." : "No reading received yet.",
                    ["subjectType"] = "Device",
                    ["subjectId"] = d.Id.ToString(),
                    ["zoneId"] = d.ZoneId.ToString(),
                };
                if (await support.RaiseAlertAsync(s, AlertType.StaleDevice, AlertSeverity.Warning, key, details, ct))
                {
                    await s.Db.Database.ExecuteSqlAsync($"""
                        UPDATE ti.alert SET warehouse_id = {d.WarehouseId}, zone_id = {d.ZoneId} WHERE dedupe_key = {key} AND status <> 'Closed'
                        """, ct);
                    s.Publish("AlertRaised", new { type = nameof(AlertType.StaleDevice), deviceId = d.Id, zoneId = d.ZoneId });
                    raised++;
                }
            }
            else
            {
                await s.Db.Database.ExecuteSqlAsync($"""
                    UPDATE ti.alert SET status = 'Closed', closed_at = {s.Now}, version = version + 1, updated_at = {s.Now}
                    WHERE dedupe_key = {key} AND status <> 'Closed'
                    """, ct);
            }
        }
        return raised;
    }

    private sealed record DeviceRow(Guid Id, string Name, Guid ZoneId, Guid WarehouseId, DateTimeOffset? LastReadingAt, bool Stale);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Raised {Count} stale device alerts in tenant {TenantId}")]
    private static partial void LogRaised(ILogger logger, int count, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Stale device evaluation failed in tenant {TenantId}; retried next run")]
    private static partial void LogFailed(ILogger logger, Exception error, Guid tenantId);
}
