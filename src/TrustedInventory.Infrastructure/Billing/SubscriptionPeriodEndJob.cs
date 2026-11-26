using System.Data;
using Npgsql;
using TrustedInventory.Application.Jobs;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Infrastructure.Audit;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Billing;

/// <summary>
/// Records the end of a cancelled subscription once its paid period is over (BIL-008, DEC-068). Bookkeeping only: access was already
/// billing-restricted at period_end by computation (DEC-033), so a late or missing run never extends service.
/// </summary>
public sealed class SubscriptionPeriodEndJob(ControlDataSource control, TimeProvider clock) : IJob
{
    public const string Subject = "system:billing-period-end";

    public async Task RunOnceAsync(CancellationToken ct) => await EndDueAsync(ct);

    /// <summary>Returns how many subscriptions were ended.</summary>
    public async Task<int> EndDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = new List<Guid>();
        await using (var cmd = control.Source.CreateCommand("""
            SELECT id FROM cp.subscription WHERE state IN ('Active','PastDue') AND cancel_at_period_end AND period_end <= @now ORDER BY period_end LIMIT 200
            """))
        {
            cmd.Parameters.AddWithValue("now", now);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                due.Add(r.GetGuid(0));
        }

        var ended = 0;
        foreach (var id in due)
        {
            await using var conn = await control.Source.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            Guid tenant;
            long revision;
            string before;
            await using (var end = new NpgsqlCommand("""
                WITH old AS (SELECT state::text AS state FROM cp.subscription WHERE id = @id FOR UPDATE)
                UPDATE cp.subscription s SET state = 'Cancelled', cancelled_at = @now, grace_end = NULL, revision = revision + 1, updated_at = @now
                FROM old WHERE s.id = @id AND s.state IN ('Active','PastDue') AND s.cancel_at_period_end AND s.period_end <= @now
                RETURNING s.tenant_id, s.revision, old.state
                """, conn, tx))
            {
                end.Parameters.AddWithValue("id", id);
                end.Parameters.AddWithValue("now", now);
                await using var r = await end.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                    continue; // resumed or ended meanwhile
                (tenant, revision, before) = (r.GetGuid(0), r.GetInt64(1), r.GetString(2));
            }
            await Sql.ExecAsync(conn, tx, """
                UPDATE cp.subscription_change SET state = 'Cancelled', updated_at = @now, version = version + 1
                WHERE subscription_id = @id AND state IN ('Pending','Scheduled')
                """, ct, ("now", now), ("id", id));
            await AuditAppender.AppendAsync(conn, tx, tenant, new AuditActor(Subject, ActorType.System, null, null), now,
                [new AuditEntry("SubscriptionEnded", "Subscription", id, (int)revision, "Paid period ended after cancellation", new { state = before },
                    new { state = "Cancelled" })], ct);
            await tx.CommitAsync(ct);
            ended++;
        }
        return ended;
    }
}
