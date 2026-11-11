using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using TrustedInventory.Application.Billing;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Jobs;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Saas;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Infrastructure.Audit;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Billing;

/// <summary>
/// Applies verified billing events from cp.webhook_inbox (BIL-005, DEC-064) on the ti_control pool. One transaction per
/// event locks the inbox row (SKIP LOCKED: never two workers on one event), applies every effect, appends the tenant's
/// audit events last and sets the final state. A failure rolls back and is recorded with backoff in a second transaction.
/// </summary>
public sealed partial class BillingEventJob(ControlDataSource control, IBillingWebhooks webhooks, InboxCipher cipher, TimeProvider clock,
    ILogger<BillingEventJob> log) : IJob
{
    public const string Subject = "system:billing-webhook";
    public const int MaxAttempts = 10;
    private const int Batch = 50;

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var due = new List<Guid>();
        await using (var cmd = control.Source.CreateCommand("""
            SELECT id FROM cp.webhook_inbox
            WHERE processing_state IN ('Received','Failed') AND attempts < @max AND next_attempt_at <= @now
            ORDER BY received_at, id LIMIT @batch
            """))
        {
            cmd.Parameters.AddWithValue("max", MaxAttempts);
            cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
            cmd.Parameters.AddWithValue("batch", Batch);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                due.Add(r.GetGuid(0));
        }
        foreach (var id in due)
            await ProcessAsync(id, ct);

        var backlog = await BacklogAsync(ct);
        if ((backlog.OldestPending is { } oldest && clock.GetUtcNow() - oldest > BacklogAge) || backlog.Exhausted > 0)
            BillingAlerts.Raise(log, "WebhookBacklog",
                $"Oldest unapplied billing event received {backlog.OldestPending:O}; {backlog.Exhausted} events exhausted their retries (SOP-003).");
    }

    /// <summary>Alert threshold for the oldest unapplied event (SOP-003).</summary>
    public static readonly TimeSpan BacklogAge = TimeSpan.FromMinutes(5);

    /// <summary>The oldest event still to apply and the events that used all their attempts (DEC-066).</summary>
    public async Task<WebhookBacklog> BacklogAsync(CancellationToken ct)
    {
        await using var cmd = control.Source.CreateCommand("""
            SELECT min(received_at) FILTER (WHERE processing_state IN ('Received','Failed') AND attempts < @max),
                   count(*) FILTER (WHERE processing_state = 'Failed' AND attempts >= @max)
            FROM cp.webhook_inbox
            """);
        cmd.Parameters.AddWithValue("max", MaxAttempts);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new WebhookBacklog(r.IsDBNull(0) ? null : r.GetFieldValue<DateTimeOffset>(0), r.GetInt64(1));
    }

    /// <summary>Processes one inbox row if it is still due. Returns the state it ended in, or null when skipped.</summary>
    public async Task<WebhookProcessingState?> ProcessAsync(Guid inboxId, CancellationToken ct)
    {
        string? eventId = null;
        try
        {
            await using var conn = await control.Source.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            string provider;
            byte[] payload;
            await using (var cmd = new NpgsqlCommand("""
                SELECT provider, event_id, payload_encrypted FROM cp.webhook_inbox
                WHERE id = @id AND processing_state IN ('Received','Failed') AND attempts < @max
                FOR UPDATE SKIP LOCKED
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("id", inboxId);
                cmd.Parameters.AddWithValue("max", MaxAttempts);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                    return null;
                provider = r.GetString(0);
                eventId = r.GetString(1);
                payload = r.GetFieldValue<byte[]>(2);
            }

            var ev = webhooks.Parse(cipher.Decrypt(provider, eventId, payload));
            var now = clock.GetUtcNow();
            var apply = new Apply(conn, tx, provider, ev, now, ct);
            var (state, tenantId, failureCode) = await apply.RunAsync(ct);
            if (tenantId is { } t && apply.Audit.Count > 0)
                await AuditAppender.AppendAsync(conn, tx, t, new AuditActor(Subject, ActorType.System, null, ev.EventId), now, apply.Audit, ct);

            await using (var done = new NpgsqlCommand("""
                UPDATE cp.webhook_inbox SET processing_state = @s::cp.webhook_processing_state, tenant_id = @t, failure_code = @f,
                       processed_at = CASE WHEN @s IN ('Processed','Ignored') THEN @now END, last_error = NULL
                WHERE id = @id
                """, conn, tx))
            {
                done.Parameters.AddWithValue("s", state.ToString());
                done.Parameters.AddWithValue("t", (object?)tenantId ?? DBNull.Value);
                done.Parameters.AddWithValue("f", (object?)failureCode ?? DBNull.Value);
                done.Parameters.AddWithValue("now", now);
                done.Parameters.AddWithValue("id", inboxId);
                await done.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            LogProcessed(log, ev.EventId, ev.EventType, state, tenantId);
            return state;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogFailed(log, e, eventId ?? inboxId.ToString());
            await RecordFailureAsync(inboxId, e, ct);
            return WebhookProcessingState.Failed;
        }
    }

    private async Task RecordFailureAsync(Guid inboxId, Exception error, CancellationToken ct)
    {
        await using var cmd = control.Source.CreateCommand("""
            UPDATE cp.webhook_inbox SET processing_state = 'Failed', attempts = attempts + 1, failure_code = 'PROCESSING_ERROR',
                   last_error = @e, next_attempt_at = @now + make_interval(secs => LEAST(30 * power(2, attempts), 3600))
            WHERE id = @id AND processing_state IN ('Received','Failed') AND attempts < @max
            """);
        // The exception type only: messages may quote payload values (section 27).
        cmd.Parameters.AddWithValue("e", error.GetType().Name);
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
        cmd.Parameters.AddWithValue("id", inboxId);
        cmd.Parameters.AddWithValue("max", MaxAttempts);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Billing event {EventId} ({EventType}) {State} for tenant {TenantId}")]
    private static partial void LogProcessed(ILogger logger, string eventId, string eventType, WebhookProcessingState state, Guid? tenantId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Billing event {EventId} failed; it will be retried with backoff")]
    private static partial void LogFailed(ILogger logger, Exception error, string eventId);

    /// <summary>The effects of one event inside the event's transaction.</summary>
    private sealed class Apply(NpgsqlConnection conn, NpgsqlTransaction tx, string provider, BillingEvent ev, DateTimeOffset now, CancellationToken ct)
    {
        public List<AuditEntry> Audit { get; } = [];

        private Guid _tenant;
        private Guid _account;

        public async Task<(WebhookProcessingState, Guid?, string?)> RunAsync(CancellationToken ct)
        {
            if (ev.Kind == BillingEventKind.Unsupported)
                return (WebhookProcessingState.Ignored, null, null);

            // 1. The tenant comes from our billing account for this provider customer, never from the payload.
            var account = ev.CustomerRef is null ? null : await RowAsync("SELECT tenant_id, id FROM cp.billing_account WHERE provider = @p AND provider_customer_id = @c FOR UPDATE",
                r => (Tenant: r.GetGuid(0), Account: r.GetGuid(1)), ("p", provider), ("c", ev.CustomerRef));
            if (account is null)
            {
                await TaskAsync(ReconciliationTaskKind.UnknownBillingReference, null, $"unknown-customer:{provider}:{ev.CustomerRef ?? ev.EventId}",
                    $"Event {ev.EventId} ({ev.EventType}) names a customer reference we do not hold.", ct);
                return (WebhookProcessingState.Quarantined, null, "UNKNOWN_REFERENCE");
            }
            (_tenant, _account) = account.Value;

            // 2. Every other reference we already hold must belong to the same tenant.
            if (!await OwnedAsync("SELECT tenant_id FROM cp.subscription WHERE provider = @p AND provider_subscription_ref = @r", ev.SubscriptionRef, ct)
                || !await OwnedAsync("SELECT tenant_id FROM cp.checkout_session WHERE provider = @p AND provider_session_ref = @r", ev.CheckoutSessionRef, ct)
                || !await OwnedAsync("SELECT tenant_id FROM cp.invoice WHERE provider = @p AND provider_invoice_ref = @r", ev.Invoice?.Ref, ct)
                || !await OwnedAsync("SELECT tenant_id FROM cp.payment WHERE provider = @p AND provider_payment_ref = @r",
                    ev.Payment?.RefundOfRef ?? ev.Dispute?.PaymentRef, ct))
            {
                await TaskAsync(ReconciliationTaskKind.UnknownBillingReference, null, $"reference-mismatch:{provider}:{ev.EventId}",
                    $"Event {ev.EventId} ({ev.EventType}) mixes references of different billing accounts.", ct);
                return (WebhookProcessingState.Quarantined, null, "REFERENCE_MISMATCH");
            }

            // 3. Terminal deletion wins over any late billing event; nothing is created or reactivated (LFC-001).
            var lifecycle = await RowAsync("SELECT lifecycle::text FROM ti.tenant WHERE id = @t", r => r.IsDBNull(0) ? "Ready" : r.GetString(0), ("t", _tenant));
            if (lifecycle == nameof(TenantLifecycle.Deleted))
            {
                await TaskAsync(ReconciliationTaskKind.SubscriptionMismatch, _tenant, $"deleted-tenant:{provider}:{ev.EventId}",
                    $"Event {ev.EventId} ({ev.EventType}) arrived for a deleted workspace; review for refund.", ct);
                return (WebhookProcessingState.Ignored, _tenant, "TENANT_DELETED");
            }

            return ev.Kind switch
            {
                BillingEventKind.InvoicePaid or BillingEventKind.InvoicePaymentFailed => await InvoiceAsync(ct),
                BillingEventKind.DisputeOpened or BillingEventKind.ChargebackRecorded => await DisputeAsync(ct),
                BillingEventKind.PaymentRefunded => await RefundAsync(ct),
                BillingEventKind.SubscriptionEnded => await EndedAsync(ct),
                _ => (WebhookProcessingState.Ignored, _tenant, null),
            };
        }

        // ---------------------------------------------------------------------------------------------------------
        // Invoices
        // ---------------------------------------------------------------------------------------------------------

        private async Task<(WebhookProcessingState, Guid?, string?)> InvoiceAsync(CancellationToken ct)
        {
            var invoice = ev.Invoice!;
            var paid = ev.Kind == BillingEventKind.InvoicePaid;
            var sub = await SubscriptionAsync(ct) ?? throw new InvalidOperationException("The workspace has no subscription (SAS-001).");
            Guid? linkedSubscription = null;

            if (invoice.Initial)
            {
                var checkout = await RowAsync("""
                    SELECT id, price_version_id, status::text FROM cp.checkout_session
                    WHERE tenant_id = @t AND provider = @p AND provider_session_ref = @r FOR UPDATE
                    """, r => (Id: r.GetGuid(0), Price: r.GetGuid(1), Status: r.GetString(2)), ("t", _tenant), ("p", provider), ("r", ev.CheckoutSessionRef!));
                if (checkout is null)
                {
                    await TaskAsync(ReconciliationTaskKind.UnknownBillingReference, _tenant, $"unknown-checkout:{provider}:{ev.CheckoutSessionRef}",
                        $"Event {ev.EventId} ({ev.EventType}) names a checkout session we do not hold.", ct);
                    return (WebhookProcessingState.Quarantined, _tenant, "UNKNOWN_REFERENCE");
                }
                if (paid)
                    linkedSubscription = await ActivateAsync(sub, checkout.Value.Id, checkout.Value.Price, checkout.Value.Status, invoice, ct);
                // A failed or abandoned initial payment leaves the trial or end state and the session as they were (BIL-003).
            }
            else
            {
                if (sub.Facts.ProviderSubscriptionRef != ev.SubscriptionRef)
                {
                    await TaskAsync(ReconciliationTaskKind.UnknownBillingReference, _tenant, $"unknown-subscription:{provider}:{ev.SubscriptionRef}",
                        $"Event {ev.EventId} ({ev.EventType}) names a provider subscription this workspace is not linked to.", ct);
                    return (WebhookProcessingState.Quarantined, _tenant, "UNKNOWN_REFERENCE");
                }
                linkedSubscription = sub.Id;
                if (paid)
                    await RenewalPaidAsync(sub, invoice, ct);
                else
                    await RenewalFailedAsync(sub, invoice, ct);
            }

            var invoiceId = await UpsertInvoiceAsync(invoice, linkedSubscription, ct);
            await PaymentAsync(invoiceId, invoice, ev.Payment!, null, ct);
            return (WebhookProcessingState.Processed, _tenant, null);
        }

        private async Task<Guid?> ActivateAsync(Sub sub, Guid checkoutId, Guid priceId, string checkoutStatus, ProviderInvoice invoice, CancellationToken ct)
        {
            switch (BillingEventRules.Activation(sub.Facts, ev.SubscriptionRef!))
            {
                case ActivationOutcome.AlreadyActive:
                    return sub.Id; // a resend: activated once already (AT-BIL-01)
                case ActivationOutcome.Conflict:
                    // Paid twice, or paid while another provider subscription is linked: never a second activation.
                    await TaskAsync(ReconciliationTaskKind.SubscriptionMismatch, _tenant, $"activation-conflict:{provider}:{ev.SubscriptionRef}",
                        $"A paid checkout ({ev.CheckoutSessionRef}) arrived while the subscription is {sub.Facts.State} and linked to another provider subscription.", ct);
                    return null;
            }

            var plan = await RowAsync("SELECT plan_version_id FROM cp.price_version WHERE id = @p", r => r.GetGuid(0), ("p", priceId))
                ?? throw new InvalidOperationException("The checkout's price no longer exists.");
            var revision = await RowAsync("""
                UPDATE cp.subscription SET state = 'Active', plan_version_id = @plan, price_version_id = @price, period_start = @ps, period_end = @pe,
                       grace_end = NULL, cancel_at_period_end = false, cancelled_at = NULL, provider = @p, provider_subscription_ref = @ref,
                       provider_revision = @rev, revision = revision + 1, updated_at = @now
                WHERE id = @id RETURNING revision
                """, r => r.GetInt64(0), ("plan", plan), ("price", priceId), ("ps", invoice.PeriodStart!.Value), ("pe", invoice.PeriodEnd!.Value),
                ("p", provider), ("ref", ev.SubscriptionRef!), ("rev", ev.SubscriptionRevision!.Value), ("now", now), ("id", sub.Id));

            if (checkoutStatus != nameof(CheckoutStatus.Completed))
            {
                await ExecAsync("UPDATE cp.checkout_session SET status = 'Completed', completed_at = @now WHERE id = @id", ("now", now), ("id", checkoutId));
                Audit.Add(new AuditEntry("CheckoutCompleted", "CheckoutSession", checkoutId, null, null, new { status = checkoutStatus },
                    new { status = "Completed", providerEventId = ev.EventId }));
            }

            // A new current entitlement snapshot from the purchased plan (ENT-002); the trial snapshot is closed.
            var version = await EntitlementSnapshots.ReplaceAsync(conn, tx, _tenant, revision!.Value, plan, now, ct);

            Audit.Add(new AuditEntry("SubscriptionActivated", "Subscription", sub.Id, (int)revision.Value, null,
                new { state = sub.Facts.State.ToString() },
                new
                {
                    state = "Active", planVersionId = plan, priceVersionId = priceId, periodStart = invoice.PeriodStart, periodEnd = invoice.PeriodEnd,
                    entitlementVersion = version, providerEventId = ev.EventId,
                }));
            await NoticeAsync(BillingNoticeKind.SubscriptionActivated, $"activated:{sub.Id}:{ev.SubscriptionRef}",
                new { planVersionId = plan, periodEnd = invoice.PeriodEnd }, ct);
            return sub.Id;
        }

        private async Task RenewalPaidAsync(Sub sub, ProviderInvoice invoice, CancellationToken ct)
        {
            var otherDues = await RowAsync("""
                SELECT EXISTS (SELECT 1 FROM cp.invoice WHERE tenant_id = @t AND subscription_id = @s AND status IN ('Open','Uncollectible')
                               AND provider_invoice_ref <> @r)
                """, r => r.GetBoolean(0), ("t", _tenant), ("s", sub.Id), ("r", invoice.Ref)) ?? false;
            if (invoice.PeriodStart is null || invoice.PeriodEnd is null)
                throw new InvalidOperationException("A paid renewal invoice needs its period.");
            var next = BillingEventRules.RenewalPaid(sub.Facts, ev.SubscriptionRevision!.Value, invoice.PeriodStart.Value, invoice.PeriodEnd.Value, otherDues);
            if (next is null)
                return; // older than what we hold: an old event cannot move the period (BIL-005)
            var revision = await SaveAsync(sub, next, ct);
            if (next.PeriodEnd != sub.Facts.PeriodEnd)
                Audit.Add(new AuditEntry("SubscriptionRenewed", "Subscription", sub.Id, (int)revision, null,
                    new { periodStart = sub.Facts.PeriodStart, periodEnd = sub.Facts.PeriodEnd },
                    new { periodStart = next.PeriodStart, periodEnd = next.PeriodEnd, providerEventId = ev.EventId }));
            if (sub.Facts.State == SubscriptionState.PastDue && next.State == SubscriptionState.Active)
            {
                Audit.Add(new AuditEntry("PaymentRecovered", "Subscription", sub.Id, (int)revision, null,
                    new { state = "PastDue", graceEnd = sub.Facts.GraceEnd }, new { state = "Active", providerEventId = ev.EventId }));
                await NoticeAsync(BillingNoticeKind.PaymentRecovered, $"recovered:{sub.Id}:{invoice.Ref}", new { invoice = invoice.Number }, ct);
            }
        }

        private async Task RenewalFailedAsync(Sub sub, ProviderInvoice invoice, CancellationToken ct)
        {
            var due = invoice.DueAt ?? sub.Facts.PeriodEnd ?? now;
            var next = BillingEventRules.RenewalFailed(sub.Facts, ev.SubscriptionRevision!.Value, due);
            if (next is null)
                return; // older than a newer success, or nothing to restrict (AT-BIL-02)
            var revision = await SaveAsync(sub, next, ct);
            if (sub.Facts.State == SubscriptionState.Active && next.State == SubscriptionState.PastDue)
            {
                Audit.Add(new AuditEntry("SubscriptionPastDue", "Subscription", sub.Id, (int)revision, null, new { state = "Active" },
                    new { state = "PastDue", graceEnd = next.GraceEnd, providerEventId = ev.EventId }));
                await NoticeAsync(BillingNoticeKind.PaymentFailed, $"payment-failed:{sub.Id}:{invoice.Ref}",
                    new { invoice = invoice.Number, graceEnd = next.GraceEnd }, ct);
            }
        }

        private async Task<long> SaveAsync(Sub sub, SubscriptionFacts next, CancellationToken ct) =>
            (await RowAsync("""
                UPDATE cp.subscription SET state = @state::cp.subscription_state, period_start = @ps, period_end = @pe, grace_end = @grace,
                       provider_revision = @rev, revision = revision + 1, updated_at = @now
                WHERE id = @id RETURNING revision
                """, r => r.GetInt64(0), ("state", next.State.ToString()), ("ps", (object?)next.PeriodStart ?? DBNull.Value),
                ("pe", (object?)next.PeriodEnd ?? DBNull.Value), ("grace", (object?)next.GraceEnd ?? DBNull.Value), ("rev", next.ProviderRevision!.Value),
                ("now", now), ("id", sub.Id)))!.Value;

        private async Task<Guid> UpsertInvoiceAsync(ProviderInvoice invoice, Guid? subscriptionId, CancellationToken ct)
        {
            var existing = await RowAsync("SELECT id, status::text, provider_revision FROM cp.invoice WHERE provider = @p AND provider_invoice_ref = @r FOR UPDATE",
                r => (Id: r.GetGuid(0), Status: Enum.Parse<InvoiceStatus>(r.GetString(1)), Revision: r.IsDBNull(2) ? (long?)null : r.GetInt64(2)),
                ("p", provider), ("r", invoice.Ref));
            var paidAt = invoice.Status == InvoiceStatus.Paid ? invoice.PaidAt ?? now : (DateTimeOffset?)null;
            if (existing is null)
            {
                var id = Guid.CreateVersion7();
                await ExecAsync("""
                    INSERT INTO cp.invoice (id, tenant_id, billing_account_id, subscription_id, provider, provider_invoice_ref, number, currency, exponent, lines,
                                            subtotal, discount, tax, total, period_start, period_end, status, issued_at, due_at, paid_at, provider_revision,
                                            created_at, updated_at)
                    VALUES (@id, @t, @a, @s, @p, @r, @n, @cur, @exp, @lines, @sub, @disc, @tax, @total, @ps, @pe, @st::cp.invoice_status, @iss, @due, @paid, @rev,
                            @now, @now)
                    """, ("id", id), ("t", _tenant), ("a", _account), ("s", (object?)subscriptionId ?? DBNull.Value), ("p", provider), ("r", invoice.Ref),
                    ("n", (object?)invoice.Number ?? DBNull.Value), ("cur", invoice.Currency), ("exp", invoice.Exponent),
                    ("lines", new Jsonb(JsonSerializer.Serialize(invoice.Lines, JsonDefaults.Options))), ("sub", invoice.Subtotal), ("disc", invoice.Discount),
                    ("tax", invoice.Tax), ("total", invoice.Total), ("ps", (object?)invoice.PeriodStart ?? DBNull.Value),
                    ("pe", (object?)invoice.PeriodEnd ?? DBNull.Value), ("st", invoice.Status.ToString()), ("iss", (object?)invoice.IssuedAt ?? DBNull.Value),
                    ("due", (object?)invoice.DueAt ?? DBNull.Value), ("paid", (object?)paidAt ?? DBNull.Value), ("rev", invoice.Revision), ("now", now));
                return id;
            }
            if (BillingEventRules.InvoiceChanges(existing.Value.Status, existing.Value.Revision, invoice.Revision))
                await ExecAsync("""
                    UPDATE cp.invoice SET status = @st::cp.invoice_status, paid_at = @paid, due_at = coalesce(@due, due_at), provider_revision = @rev,
                           subscription_id = coalesce(subscription_id, @s), updated_at = @now
                    WHERE id = @id
                    """, ("st", invoice.Status.ToString()), ("paid", (object?)paidAt ?? DBNull.Value), ("due", (object?)invoice.DueAt ?? DBNull.Value),
                    ("rev", invoice.Revision), ("s", (object?)subscriptionId ?? DBNull.Value), ("now", now), ("id", existing.Value.Id));
            return existing.Value.Id;
        }

        private async Task<Guid?> PaymentAsync(Guid invoiceId, ProviderInvoice invoice, ProviderPayment payment, Guid? refundOf, CancellationToken ct)
        {
            var id = await RowAsync("""
                INSERT INTO cp.payment (id, tenant_id, invoice_id, provider, provider_payment_ref, provider_event_id, status, currency, exponent, amount,
                                        refund_of_id, provider_occurred_at, recorded_at)
                VALUES (@id, @t, @i, @p, @r, @e, @st::cp.payment_status, @cur, @exp, @amount, @refund, @at, @now)
                ON CONFLICT (provider, provider_payment_ref) DO NOTHING RETURNING id
                """, r => r.GetGuid(0), ("id", Guid.CreateVersion7()), ("t", _tenant), ("i", invoiceId), ("p", provider), ("r", payment.Ref), ("e", ev.EventId),
                ("st", payment.Status.ToString()), ("cur", invoice.Currency), ("exp", invoice.Exponent), ("amount", payment.Amount),
                ("refund", (object?)refundOf ?? DBNull.Value), ("at", payment.OccurredAt), ("now", now));
            if (id is not null)
                Audit.Add(new AuditEntry(payment.Status switch
                {
                    PaymentStatus.Failed => "PaymentFailed",
                    PaymentStatus.Refunded => "RefundRecorded",
                    _ => "PaymentRecorded",
                }, "Payment", id, null, null, null, new
                {
                    invoiceId, status = payment.Status.ToString(), amount = new MoneyDto(invoice.Currency, payment.Amount, invoice.Exponent),
                    providerEventId = ev.EventId,
                }));
            return id;
        }

        /// <summary>The provider ended the subscription after a cancellation (DEC-065). Access was already restricted by computation.</summary>
        private async Task<(WebhookProcessingState, Guid?, string?)> EndedAsync(CancellationToken ct)
        {
            var sub = await SubscriptionAsync(ct) ?? throw new InvalidOperationException("The workspace has no subscription (SAS-001).");
            if (sub.Facts.ProviderSubscriptionRef != ev.SubscriptionRef)
            {
                await TaskAsync(ReconciliationTaskKind.UnknownBillingReference, _tenant, $"unknown-subscription:{provider}:{ev.SubscriptionRef}",
                    $"Event {ev.EventId} ({ev.EventType}) names a provider subscription this workspace is not linked to.", ct);
                return (WebhookProcessingState.Quarantined, _tenant, "UNKNOWN_REFERENCE");
            }
            var next = PlanChangeRules.Ended(sub.Facts, ev.SubscriptionRevision!.Value);
            if (next is null)
                return (WebhookProcessingState.Processed, _tenant, null);
            var revision = await RowAsync("""
                UPDATE cp.subscription SET state = 'Cancelled', cancelled_at = @now, grace_end = NULL, provider_revision = @rev, revision = revision + 1,
                       updated_at = @now
                WHERE id = @id RETURNING revision
                """, r => r.GetInt64(0), ("now", now), ("rev", next.ProviderRevision!.Value), ("id", sub.Id));
            await ExecAsync("""
                UPDATE cp.subscription_change SET state = 'Cancelled', updated_at = @now, version = version + 1
                WHERE subscription_id = @id AND state IN ('Pending','Scheduled')
                """, ("now", now), ("id", sub.Id));
            Audit.Add(new AuditEntry("SubscriptionEnded", "Subscription", sub.Id, (int)revision!.Value, null, new { state = sub.Facts.State.ToString() },
                new { state = "Cancelled", providerEventId = ev.EventId }));
            return (WebhookProcessingState.Processed, _tenant, null);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Disputes, chargebacks and refunds: records and an operator task, never an access or stock change (BIL-008)
        // ---------------------------------------------------------------------------------------------------------

        private async Task<(WebhookProcessingState, Guid?, string?)> DisputeAsync(CancellationToken ct)
        {
            var dispute = ev.Dispute!;
            var payment = await RowAsync("SELECT id FROM cp.payment WHERE tenant_id = @t AND provider = @p AND provider_payment_ref = @r",
                r => r.GetGuid(0), ("t", _tenant), ("p", provider), ("r", dispute.PaymentRef));
            if (payment is null)
            {
                await TaskAsync(ReconciliationTaskKind.UnknownBillingReference, _tenant, $"unknown-payment:{provider}:{dispute.PaymentRef}",
                    $"Event {ev.EventId} ({ev.EventType}) names a payment we do not hold.", ct);
                return (WebhookProcessingState.Quarantined, _tenant, "UNKNOWN_REFERENCE");
            }
            var chargeback = ev.Kind == BillingEventKind.ChargebackRecorded;
            var opened = await TaskAsync(chargeback ? ReconciliationTaskKind.Chargeback : ReconciliationTaskKind.Dispute, _tenant,
                $"{(chargeback ? "chargeback" : "dispute")}:{provider}:{dispute.Ref}",
                $"{(chargeback ? "Chargeback" : "Dispute")} {dispute.Ref} on payment {dispute.PaymentRef}: review; access is unchanged.", ct);
            if (opened)
                Audit.Add(new AuditEntry(chargeback ? "ChargebackRecorded" : "BillingDisputeOpened", "Payment", payment, null, null, null,
                    new { dispute = dispute.Ref, amountMinorUnits = dispute.Amount, providerEventId = ev.EventId }));
            return (WebhookProcessingState.Processed, _tenant, null);
        }

        private async Task<(WebhookProcessingState, Guid?, string?)> RefundAsync(CancellationToken ct)
        {
            var refund = ev.Payment!;
            var original = await RowAsync("""
                SELECT p.id, p.invoice_id, i.currency::text, i.exponent FROM cp.payment p JOIN cp.invoice i ON i.id = p.invoice_id
                WHERE p.tenant_id = @t AND p.provider = @p AND p.provider_payment_ref = @r
                """, r => (Id: r.GetGuid(0), Invoice: r.GetGuid(1), Currency: r.GetString(2).Trim(), Exponent: r.GetInt16(3)),
                ("t", _tenant), ("p", provider), ("r", refund.RefundOfRef!));
            if (original is null)
            {
                await TaskAsync(ReconciliationTaskKind.UnknownBillingReference, _tenant, $"unknown-payment:{provider}:{refund.RefundOfRef}",
                    $"Event {ev.EventId} ({ev.EventType}) refunds a payment we do not hold.", ct);
                return (WebhookProcessingState.Quarantined, _tenant, "UNKNOWN_REFERENCE");
            }
            var o = original.Value;
            var shape = new ProviderInvoice("", null, o.Currency, o.Exponent, [], 0, 0, 0, 0, null, null, InvoiceStatus.Paid, null, null, null, 0, false);
            await PaymentAsync(o.Invoice, shape, refund with { Status = PaymentStatus.Refunded }, o.Id, ct);
            await TaskAsync(ReconciliationTaskKind.Refund, _tenant, $"refund:{provider}:{refund.Ref}",
                $"Refund {refund.Ref} of payment {refund.RefundOfRef}: review; access is unchanged.", ct);
            return (WebhookProcessingState.Processed, _tenant, null);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------------------------------------

        private sealed record Sub(Guid Id, SubscriptionFacts Facts);

        private Task<Sub?> SubscriptionAsync(CancellationToken ct) =>
            RowAsync("""
                SELECT id, state::text, provider_subscription_ref, provider_revision, period_start, period_end, grace_end
                FROM cp.subscription WHERE tenant_id = @t FOR UPDATE
                """, r => new Sub(r.GetGuid(0), new SubscriptionFacts(Enum.Parse<SubscriptionState>(r.GetString(1)), r.IsDBNull(2) ? null : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
                    r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6))),
                ("t", _tenant));

        private async Task<bool> OwnedAsync(string sql, string? reference, CancellationToken ct)
        {
            if (reference is null)
                return true;
            var owner = await RowAsync(sql, r => r.GetGuid(0), ("p", provider), ("r", reference));
            return owner is null || owner == _tenant;
        }

        /// <summary>Opens one operator task per cause; returns false when it already existed.</summary>
        private async Task<bool> TaskAsync(ReconciliationTaskKind kind, Guid? tenantId, string dedupeKey, string detail, CancellationToken ct) =>
            await ExecAsync("""
                INSERT INTO cp.billing_task (kind, tenant_id, provider, dedupe_key, detail, created_at)
                VALUES (@k::cp.billing_task_kind, @t, @p, @d, @detail, @now) ON CONFLICT (dedupe_key) DO NOTHING
                """, ("k", kind.ToString()), ("t", (object?)tenantId ?? DBNull.Value), ("p", provider), ("d", Truncate(dedupeKey, 300)),
                ("detail", Truncate(detail, 2000)), ("now", now)) == 1;

        private Task<int> NoticeAsync(BillingNoticeKind kind, string dedupeKey, object detail, CancellationToken ct) =>
            ExecAsync("""
                INSERT INTO cp.billing_notice (tenant_id, kind, dedupe_key, detail, created_at)
                VALUES (@t, @k::cp.billing_notice_kind, @d, @detail, @now) ON CONFLICT (dedupe_key) DO NOTHING
                """, ("t", _tenant), ("k", kind.ToString()), ("d", Truncate(dedupeKey, 300)),
                ("detail", new Jsonb(JsonSerializer.Serialize(detail, detail.GetType(), JsonDefaults.Options))), ("now", now));

        private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

        private async Task<T?> RowAsync<T>(string sql, Func<NpgsqlDataReader, T> read, params (string Name, object Value)[] args)
            where T : struct
        {
            await using var cmd = Command(sql, args);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) ? read(r) : null;
        }

        private async Task<string?> RowAsync(string sql, Func<NpgsqlDataReader, string> read, params (string Name, object Value)[] args)
        {
            await using var cmd = Command(sql, args);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) ? read(r) : null;
        }

        private async Task<Sub?> RowAsync(string sql, Func<NpgsqlDataReader, Sub> read, params (string Name, object Value)[] args)
        {
            await using var cmd = Command(sql, args);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) ? read(r) : null;
        }

        private async Task<int> ExecAsync(string sql, params (string Name, object Value)[] args)
        {
            await using var cmd = Command(sql, args);
            return await cmd.ExecuteNonQueryAsync(ct);
        }

        private NpgsqlCommand Command(string sql, (string Name, object Value)[] args)
        {
            var cmd = new NpgsqlCommand(sql, conn, tx);
            foreach (var (name, value) in args)
                cmd.Parameters.Add(value is Jsonb j
                    ? new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = j.Json }
                    : new NpgsqlParameter(name, value));
            return cmd;
        }

        private sealed record Jsonb(string Json);
    }
}

public sealed record WebhookBacklog(DateTimeOffset? OldestPending, long Exhausted);

public enum BillingNoticeKind
{
    SubscriptionActivated,
    PaymentFailed,
    PaymentRecovered,
}
