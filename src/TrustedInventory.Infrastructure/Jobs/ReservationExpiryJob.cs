using Microsoft.Extensions.Logging;
using TrustedInventory.Application.Jobs;
using TrustedInventory.Application.Outbound;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Infrastructure.Jobs;

/// <summary>
/// Reservation expiry (STK-008, DEC-055): releases unpicked reservations past their 24 h expiry in every tenant the
/// state matrix allows. It only releases stock and never consumes it. One transaction per order; an order that fails
/// is logged and retried on the next run without holding up other orders or tenants.
/// </summary>
public sealed partial class ReservationExpiryJob(TenantJobRunner runner, ILogger<ReservationExpiryJob> log) : IJob
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var (tenantId, status) in await runner.TenantsAsync(BackgroundJob.ReservationExpiry, ct))
        {
            var uow = runner.UnitOfWorkFor(tenantId, status);
            List<Guid> orders;
            try
            {
                orders = await uow.ReadAsync(ReservationExpiry.DueOrdersAsync, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogTenantFailed(log, e, tenantId);
                continue;
            }
            var expired = 0;
            foreach (var orderId in orders)
            {
                try
                {
                    expired += await uow.WriteAsync((s, c) => ReservationExpiry.ExpireOrderAsync(s, orderId, c), ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    LogOrderFailed(log, e, orderId, tenantId);
                }
            }
            if (expired > 0)
                LogExpired(log, expired, tenantId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Expired {Count} reservations in tenant {TenantId}")]
    private static partial void LogExpired(ILogger logger, int count, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reservation expiry failed for order {OrderId} in tenant {TenantId}; retried next run")]
    private static partial void LogOrderFailed(ILogger logger, Exception error, Guid orderId, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reservation expiry could not list due orders in tenant {TenantId}")]
    private static partial void LogTenantFailed(ILogger logger, Exception error, Guid tenantId);
}
