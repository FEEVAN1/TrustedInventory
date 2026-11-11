using Microsoft.Extensions.Logging;
using TrustedInventory.Application.Billing;
using TrustedInventory.Application.Jobs;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Billing;

/// <summary>
/// Platform billing alerts (SOP-003, DEC-066), raised as structured error logs. Persisted platform alert records are S3.
/// </summary>
public static partial class BillingAlerts
{
    [LoggerMessage(Level = LogLevel.Error, Message = "[BILLING ALERT] {AlertType}: {Detail}")]
    public static partial void Raise(ILogger logger, string alertType, string detail);
}

/// <summary>Outcome of one reconciliation run, for tests and logs.</summary>
public sealed record ReconciliationResult(bool ProviderReachable, int EventsFetched, int Mismatches, int UnpaidInvoices, int EntitlementMismatches);

/// <summary>
/// Daily reconciliation with the provider (BIL-005, BIL-006, AT-SOP-01, DEC-066): catch up on events after the stored cursor through
/// the normal inbox and processor, then compare subscriptions, unpaid invoices and entitlement snapshots and open one operator task
/// per difference. A provider outage changes nothing: access never depends on the provider.
/// </summary>
public sealed partial class BillingReconciliationJob(ControlDataSource control, IBillingProvider provider, WebhookInbox inbox, BillingEventJob events,
    TimeProvider clock, ILogger<BillingReconciliationJob> log) : IJob
{
    private const int PageSize = 100;

    public async Task RunOnceAsync(CancellationToken ct) => await RunAsync(ct);

    public async Task<ReconciliationResult> RunAsync(CancellationToken ct)
    {
        await using (var ensure = control.Source.CreateCommand("INSERT INTO cp.billing_sync_state (provider) VALUES (@p) ON CONFLICT DO NOTHING"))
        {
            ensure.Parameters.AddWithValue("p", provider.Code);
            await ensure.ExecuteNonQueryAsync(ct);
        }
        string? cursor;
        await using (var read = control.Source.CreateCommand("SELECT cursor FROM cp.billing_sync_state WHERE provider = @p"))
        {
            read.Parameters.AddWithValue("p", provider.Code);
            cursor = await read.ExecuteScalarAsync(ct) as string;
        }

        // 1. Catch up on provider events, through the same inbox and processor as webhooks.
        var fetched = 0;
        try
        {
            while (true)
            {
                var page = await provider.ListEventsSinceAsync(cursor, PageSize, ct);
                foreach (var e in page.Events)
                    await inbox.StoreFetchedAsync(provider.Code, e.Body, ct);
                fetched += page.Events.Count;
                cursor = page.NextCursor;
                await SaveCursorAsync(cursor, ct); // progress survives an outage part-way
                if (page.Events.Count < PageSize)
                    break;
            }
        }
        catch (BillingProviderUnavailableException)
        {
            await RecordAsync(success: false, ct);
            BillingAlerts.Raise(log, "BillingReconciliation", "The billing provider is unreachable; the last verified access stays in force and the next run catches up.");
            return new ReconciliationResult(false, fetched, 0, 0, 0);
        }
        await events.RunOnceAsync(ct);

        // 2. Compare what the events did not settle.
        var mismatches = 0;
        var subscriptions = new List<(Guid Tenant, string Ref, string State, DateTimeOffset? PeriodEnd, bool Cancel, long Revision)>();
        await using (var cmd = control.Source.CreateCommand("""
            SELECT s.tenant_id, s.provider_subscription_ref, s.state::text, s.period_end, s.cancel_at_period_end, s.revision
            FROM cp.subscription s JOIN ti.tenant t ON t.id = s.tenant_id
            WHERE s.provider = @p AND s.provider_subscription_ref IS NOT NULL AND t.lifecycle IS DISTINCT FROM 'Deleted'
            """))
        {
            cmd.Parameters.AddWithValue("p", provider.Code);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                subscriptions.Add((r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3), r.GetBoolean(4),
                    r.GetInt64(5)));
        }
        try
        {
            foreach (var s in subscriptions)
            {
                var theirs = await provider.FetchSubscriptionAsync(s.Ref, ct);
                var differences = new List<string>();
                if (theirs is null)
                    differences.Add("the provider does not know this subscription");
                else
                {
                    // Ended at the provider while our paid period still runs after a cancellation is consistent (DEC-068).
                    var endedEarly = theirs.State == SubscriptionState.Cancelled && s.Cancel && s.State is "Active" or "PastDue"
                        && s.PeriodEnd > clock.GetUtcNow();
                    if (!endedEarly && theirs.State.ToString() != s.State)
                        differences.Add($"state {s.State} here, {theirs.State} at the provider");
                    if (theirs.PeriodEnd is { } end && (s.PeriodEnd is null || Math.Abs((end - s.PeriodEnd.Value).TotalSeconds) >= 1))
                        differences.Add($"period end {s.PeriodEnd:O} here, {end:O} at the provider");
                    if (!endedEarly && theirs.CancelAtPeriodEnd != s.Cancel)
                        differences.Add($"pending cancellation {s.Cancel} here, {theirs.CancelAtPeriodEnd} at the provider");
                }
                if (differences.Count == 0)
                    continue;
                mismatches++;
                await TaskAsync(ReconciliationTaskKind.SubscriptionMismatch, s.Tenant, $"subscription-mismatch:{provider.Code}:{s.Ref}:{s.Revision}",
                    $"Subscription {s.Ref}: {string.Join("; ", differences)}. Review; nothing was changed automatically.", ct);
            }
        }
        catch (BillingProviderUnavailableException)
        {
            await RecordAsync(success: false, ct);
            BillingAlerts.Raise(log, "BillingReconciliation", "The billing provider became unreachable during comparison; the next run continues.");
            return new ReconciliationResult(false, fetched, mismatches, 0, 0);
        }

        var now = clock.GetUtcNow();
        var unpaid = 0;
        await using (var cmd = control.Source.CreateCommand("""
            SELECT i.tenant_id, i.provider_invoice_ref, coalesce(i.number, i.provider_invoice_ref), i.due_at FROM cp.invoice i
            WHERE i.provider = @p AND i.status IN ('Open','Uncollectible') AND i.due_at < @now
            """))
        {
            cmd.Parameters.AddWithValue("p", provider.Code);
            cmd.Parameters.AddWithValue("now", now);
            var rows = new List<(Guid Tenant, string Ref, string Number, DateTimeOffset Due)>();
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                    rows.Add((r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3)));
            foreach (var i in rows)
            {
                unpaid++;
                await TaskAsync(ReconciliationTaskKind.UnpaidInvoice, i.Tenant, $"unpaid:{provider.Code}:{i.Ref}",
                    $"Invoice {i.Number} is unpaid since {i.Due:O}.", ct);
            }
        }

        var entitlement = 0;
        await using (var cmd = control.Source.CreateCommand("""
            SELECT s.tenant_id, s.revision FROM cp.subscription s JOIN ti.tenant t ON t.id = s.tenant_id
            LEFT JOIN cp.entitlement_snapshot e ON e.tenant_id = s.tenant_id AND e.is_current
            WHERE t.lifecycle = 'Ready'
              AND (e.id IS NULL OR e.plan_version_id <> s.plan_version_id OR (e.effective_to IS NOT NULL AND e.effective_to <= @now))
            """))
        {
            cmd.Parameters.AddWithValue("now", now);
            var rows = new List<(Guid Tenant, long Revision)>();
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                    rows.Add((r.GetGuid(0), r.GetInt64(1)));
            foreach (var (tenant, revision) in rows)
            {
                entitlement++;
                if (await TaskAsync(ReconciliationTaskKind.EntitlementMismatch, tenant, $"entitlement:{tenant}:{revision}",
                        "The current entitlement snapshot is missing, expired or for another plan than the subscription.", ct))
                    BillingAlerts.Raise(log, "EntitlementMismatch", $"Tenant {tenant}: entitlement snapshot does not match subscription revision {revision}.");
            }
        }

        await RecordAsync(success: true, ct);
        LogCompleted(log, fetched, mismatches, unpaid, entitlement);
        return new ReconciliationResult(true, fetched, mismatches, unpaid, entitlement);
    }

    private async Task SaveCursorAsync(string? cursor, CancellationToken ct)
    {
        await using var cmd = control.Source.CreateCommand("UPDATE cp.billing_sync_state SET cursor = @c WHERE provider = @p");
        cmd.Parameters.AddWithValue("c", (object?)cursor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("p", provider.Code);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task RecordAsync(bool success, CancellationToken ct)
    {
        await using var cmd = control.Source.CreateCommand("""
            UPDATE cp.billing_sync_state SET last_run_at = @now, last_success_at = CASE WHEN @ok THEN @now ELSE last_success_at END,
                   last_error = CASE WHEN @ok THEN NULL ELSE 'PROVIDER_UNAVAILABLE' END
            WHERE provider = @p
            """);
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
        cmd.Parameters.AddWithValue("ok", success);
        cmd.Parameters.AddWithValue("p", provider.Code);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<bool> TaskAsync(ReconciliationTaskKind kind, Guid tenantId, string dedupeKey, string detail, CancellationToken ct)
    {
        await using var cmd = control.Source.CreateCommand("""
            INSERT INTO cp.billing_task (kind, tenant_id, provider, dedupe_key, detail, created_at)
            VALUES (@k::cp.billing_task_kind, @t, @p, @d, @detail, @now) ON CONFLICT (dedupe_key) DO NOTHING
            """);
        cmd.Parameters.AddWithValue("k", kind.ToString());
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("p", provider.Code);
        cmd.Parameters.AddWithValue("d", dedupeKey.Length <= 300 ? dedupeKey : dedupeKey[..300]);
        cmd.Parameters.AddWithValue("detail", detail.Length <= 2000 ? detail : detail[..2000]);
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Billing reconciliation: {Fetched} events fetched, {Mismatches} subscription differences, {Unpaid} unpaid invoices, {Entitlement} entitlement differences")]
    private static partial void LogCompleted(ILogger logger, int fetched, int mismatches, int unpaid, int entitlement);
}

/// <summary>
/// Payment reminders on day 3 and day 6 after the due date of a past-due subscription (BIL-007 proposal, D10, DEC-066). Day 0 is the
/// PaymentFailed notice. One notice per day per grace period; the relay delivers them (DEC-064).
/// </summary>
public sealed class BillingReminderJob(ControlDataSource control, TimeProvider clock) : IJob
{
    public static readonly int[] ReminderDays = [3, 6];

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        for (var i = 0; i < ReminderDays.Length; i++)
        {
            var day = ReminderDays[i];
            // A reminder is due from its day until the next one is: a late run sends only the latest, not every missed one.
            var until = i + 1 < ReminderDays.Length ? ReminderDays[i + 1] : 7;
            await using var cmd = control.Source.CreateCommand("""
                INSERT INTO cp.billing_notice (tenant_id, kind, dedupe_key, detail, created_at)
                SELECT s.tenant_id, 'PaymentReminder', 'reminder:' || s.id || ':' || to_char(s.grace_end AT TIME ZONE 'UTC', 'YYYYMMDDHH24MISSUS') || ':' || @day,
                       jsonb_build_object('day', @day, 'graceEnd', s.grace_end), @now
                FROM cp.subscription s JOIN ti.tenant t ON t.id = s.tenant_id
                WHERE s.state = 'PastDue' AND s.grace_end IS NOT NULL AND t.lifecycle = 'Ready'
                  AND @now >= s.grace_end - make_interval(days => 7 - @day) AND @now < s.grace_end - make_interval(days => 7 - @until)
                ON CONFLICT (dedupe_key) DO NOTHING
                """);
            cmd.Parameters.AddWithValue("day", day);
            cmd.Parameters.AddWithValue("until", until);
            cmd.Parameters.AddWithValue("now", now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
