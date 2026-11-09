using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Domain.Outbound;

namespace TrustedInventory.Application.Outbound;

/// <summary>
/// Reservation expiry (STK-008, DEC-055), run by the worker inside tenant sessions. Each order expires in its own
/// transaction, so one order that cannot be released (for example a corrupted bucket awaiting reconciliation) never
/// stops the others. Per order it takes the release locks (order row → lines → picks → reservations → buckets),
/// re-reads, and expires only segments that are still Active and past due. Picked segments never expire; dispatch
/// consumes only Picked ones, so the two never consume the same commitment.
/// </summary>
public static class ReservationExpiry
{
    /// <summary>Orders with at least one Active reservation past due (unlocked read; each order is rechecked under lock).</summary>
    public static Task<List<Guid>> DueOrdersAsync(ITenantSession s, CancellationToken ct) =>
        s.Db.Database.SqlQuery<Guid>($"""
            SELECT DISTINCT l.order_id AS "Value" FROM ti.reservation r
            JOIN ti.outbound_order_line l ON l.tenant_id = r.tenant_id AND l.id = r.order_line_id
            WHERE r.state = 'Active' AND r.expires_at <= {s.Now}
            ORDER BY 1
            """).ToListAsync(ct);

    /// <summary>Expires the order's due reservations; returns how many segments expired (0 when none are still due).</summary>
    public static async Task<int> ExpireOrderAsync(ITenantSession s, Guid orderId, CancellationToken ct)
    {
        await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.outbound_order WHERE id = {orderId} FOR UPDATE").ToListAsync(ct);
        await s.Db.Database.SqlQuery<Guid>(
            $"SELECT id AS \"Value\" FROM ti.outbound_order_line WHERE order_id = {orderId} ORDER BY line_number FOR UPDATE").ToListAsync(ct);
        var order = await s.Db.Set<OutboundOrder>().SingleAsync(o => o.Id == orderId, ct);
        var lines = await s.Db.Set<OutboundOrderLine>().Where(l => l.OrderId == orderId).ToListAsync(ct);
        var ids = await ReservationRelease.ReleaseActiveAsync(s, lines.Select(l => l.Id).ToList(), lines.ToDictionary(l => l.Id), ct,
            expiredOnly: true);
        if (ids.Count == 0)
            return 0; // already expired, picked or released by a concurrent transaction
        DomainGuard.Run(() => order.Recalculate(lines.Select(l => l.Progress).ToList(), s.Now));
        s.Audit(new AuditEntry("ReservationsExpired", "OutboundOrder", order.Id, order.Version, "Reservation expiry (STK-008)", null,
            new { orderId, expiredReservations = ids, order.Status }));
        s.Publish("ReservationsExpired", new { orderId, expiredReservations = ids });
        await s.Db.SaveChangesAsync(ct);
        return ids.Count;
    }
}
