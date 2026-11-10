using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TrustedInventory.Application.Saas;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Infrastructure;
using TrustedInventory.Infrastructure.Billing;
using TrustedInventory.Infrastructure.Jobs;
using TrustedInventory.Infrastructure.Persistence;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Billing;

/// <summary>Signed webhooks into the inbox and the worker that applies them, against the labelled simulator (DEC-064).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class WebhookTests(ApiFixture fx)
{
    private const string Url = "/api/v1/webhooks/billing/simulator";

    private SimulatedBillingProvider Simulator => fx.Factory.Services.GetRequiredService<SimulatedBillingProvider>();

    [Fact]
    [Trait("Requirement", "AT-BIL-01")]
    [Trait("Requirement", "BIL-003")]
    [Trait("Requirement", "BIL-004")]
    [Trait("Requirement", "BIL-005")]
    public async Task A_verified_paid_checkout_activates_once_however_often_it_arrives()
    {
        var t = await CheckoutAsync("wh-paid");
        var paid = Simulator.PayCheckout(t.CheckoutId);

        var ack = await (await DeliverAsync(paid)).ExpectAsync(HttpStatusCode.OK);
        Assert.True((bool)ack["received"]!);
        Assert.False((bool)ack["duplicate"]!);
        // 200 means stored, nothing more: the subscription changes only when the worker applies the event.
        Assert.Equal("Trialing", (string?)(await (await t.Owner.GetAsync("/api/v1/saas/subscription")).ExpectAsync(HttpStatusCode.OK))["state"]);

        await ProcessAsync();
        var sub = await (await t.Owner.GetAsync("/api/v1/saas/subscription")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Active", (string?)sub["state"]);
        Assert.Equal(t.PriceId.ToString(), (string?)sub["priceVersionId"]);
        Assert.Equal("Completed", (string?)(await (await t.Owner.GetAsync($"/api/v1/saas/checkout/{t.CheckoutId}")).ExpectAsync(HttpStatusCode.OK))["status"]);
        var invoices = await (await t.Owner.GetAsync("/api/v1/saas/invoices")).ExpectAsync(HttpStatusCode.OK);
        var invoice = Assert.Single(invoices["items"]!.AsArray())!;
        Assert.Equal("Paid", (string?)invoice["status"]);
        Assert.Equal("Succeeded", (string?)invoice["paymentStatus"]);
        Assert.Equal(2500L, (long)invoice["total"]!["minorUnits"]!);

        // The identical delivery is acknowledged as a duplicate; a resend under a new event id is stored but changes nothing.
        Assert.True((bool)(await (await DeliverAsync(paid)).ExpectAsync(HttpStatusCode.OK))["duplicate"]!);
        await (await DeliverAsync(Simulator.Resend(paid))).ExpectAsync(HttpStatusCode.OK);
        await ProcessAsync();

        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'SubscriptionActivated'", t.TenantId));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'PaymentRecorded'", t.TenantId));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM cp.invoice WHERE tenant_id = @t", t.TenantId));
        Assert.Equal(2L, await CountAsync("SELECT max(entitlement_version) FROM cp.entitlement_snapshot WHERE tenant_id = @t", t.TenantId));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM cp.entitlement_snapshot WHERE tenant_id = @t AND is_current", t.TenantId));
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM cp.webhook_inbox WHERE tenant_id = @t AND processing_state = 'Processed'", t.TenantId));

        // The notice reaches the tenant's outbox through the tenant's own transaction (ti_control has no outbox privilege).
        await RelayNoticesAsync();
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.outbox_event WHERE tenant_id = @t AND event_type = 'BillingSubscriptionActivated'", t.TenantId));
        await RelayNoticesAsync();
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.outbox_event WHERE tenant_id = @t AND event_type = 'BillingSubscriptionActivated'", t.TenantId));
    }

    [Fact]
    [Trait("Requirement", "AT-BIL-02")]
    [Trait("Requirement", "BIL-004")]
    public async Task Forged_unsigned_stale_and_malformed_events_are_refused_and_stored_nowhere()
    {
        var good = Simulator.Build("customer.updated", new SimEventData("sim_cus_" + Guid.NewGuid().ToString("N")));
        var forged = SimulatedBillingProvider.Sign(good.EventId, good.Type, good.Body, DateTimeOffset.UtcNow, "not-the-secret-not-the-secret-not-the-secret");
        await (await DeliverAsync(forged)).ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await (await DeliverAsync(good with { Signature = "" })).ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        var stale = SimulatedBillingProvider.Sign(good.EventId, good.Type, good.Body, DateTimeOffset.UtcNow.AddMinutes(-6), ApiFixture.SimulatorSecret);
        await (await DeliverAsync(stale)).ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        // One changed byte after signing breaks the signature over the raw body.
        var tampered = good with { Body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(good.Body).Replace("customer.updated", "customer.updatex", StringComparison.Ordinal)) };
        await (await DeliverAsync(tampered)).ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await (await DeliverAsync(good, provider: "stripe")).ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");

        // Correctly signed, but not an event: 400.
        foreach (var body in new[] { "not json", "{\"id\":\"evt_x\"}", "{\"id\":\"evt_y\",\"type\":\"invoice.paid\",\"created\":\"2026-11-10T00:00:00Z\",\"data\":{}}" })
        {
            var signed = SimulatedBillingProvider.Sign("evt", "x", Encoding.UTF8.GetBytes(body), DateTimeOffset.UtcNow, ApiFixture.SimulatorSecret);
            await (await DeliverAsync(signed)).ExpectProblemAsync(HttpStatusCode.BadRequest, "MALFORMED_REQUEST");
        }
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM cp.webhook_inbox WHERE event_id = ANY(@ids)", null,
            ("ids", new[] { good.EventId, "evt_x", "evt_y" })));

        // An unsupported type is stored and ignored. Its payload is encrypted at rest and the summary holds references only.
        await (await DeliverAsync(good)).ExpectAsync(HttpStatusCode.OK);
        await ProcessAsync();
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT processing_state::text, payload_encrypted, payload_redacted::text FROM cp.webhook_inbox WHERE event_id = @e", conn);
        cmd.Parameters.AddWithValue("e", good.EventId);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal("Ignored", r.GetString(0));
        var stored = r.GetFieldValue<byte[]>(1);
        Assert.False(stored.AsSpan().IndexOf(Encoding.UTF8.GetBytes("customer.updated")) >= 0, "payload stored in clear");
        Assert.DoesNotContain("amount", r.GetString(2), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Requirement", "AT-BIL-02")]
    [Trait("Requirement", "BIL-004")]
    public async Task Unknown_and_foreign_references_are_quarantined_and_never_trusted()
    {
        var unknownCustomer = "sim_cus_" + Guid.NewGuid().ToString("N");
        var invoice = new SimInvoice("sim_in_" + Guid.NewGuid().ToString("N"), null, "XTS", 2, [], 100, 0, 0, 100, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMonths(1), InvoiceStatus.Paid, null, null, DateTimeOffset.UtcNow, 1, Initial: true);
        var payment = new SimPayment("sim_py_" + Guid.NewGuid().ToString("N"), PaymentStatus.Succeeded, 100, DateTimeOffset.UtcNow);
        var data = new SimEventData(unknownCustomer, "sim_sub_" + Guid.NewGuid().ToString("N"), 1, "sim_cs_unknown", invoice, payment);
        var first = Simulator.Build("invoice.paid", data);
        await (await DeliverAsync(first)).ExpectAsync(HttpStatusCode.OK);
        await (await DeliverAsync(Simulator.Resend(first))).ExpectAsync(HttpStatusCode.OK);
        await ProcessAsync();
        Assert.Equal(2L, await CountAsync("""
            SELECT count(*) FROM cp.webhook_inbox WHERE payload_redacted ->> 'customer' = @c AND processing_state = 'Quarantined'
              AND failure_code = 'UNKNOWN_REFERENCE' AND tenant_id IS NULL
            """, null, ("c", unknownCustomer)));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM cp.billing_task WHERE kind = 'UnknownBillingReference' AND dedupe_key = @k", null,
            ("k", $"unknown-customer:simulator:{unknownCustomer}")));

        // Tenant A's customer with tenant B's checkout session: neither tenant is touched.
        var a = await CheckoutAsync("wh-mix-a");
        var b = await CheckoutAsync("wh-mix-b");
        var aCustomer = (string)(await ScalarAsync("SELECT provider_customer_id FROM cp.billing_account WHERE tenant_id = @t", a.TenantId))!;
        var bSession = Simulator.CheckoutFor(b.CheckoutId)!.SessionRef;
        var mixed = Simulator.Build("invoice.paid", data with { Customer = aCustomer, CheckoutSession = bSession });
        await (await DeliverAsync(mixed)).ExpectAsync(HttpStatusCode.OK);
        await ProcessAsync();
        Assert.Equal("Quarantined", await ScalarAsync("SELECT processing_state::text FROM cp.webhook_inbox WHERE event_id = @e", null, ("e", mixed.EventId)));
        Assert.Equal("REFERENCE_MISMATCH", await ScalarAsync("SELECT failure_code FROM cp.webhook_inbox WHERE event_id = @e", null, ("e", mixed.EventId)));
        foreach (var tenant in new[] { a.TenantId, b.TenantId })
            Assert.Equal("Trialing", await ScalarAsync("SELECT state::text FROM cp.subscription WHERE tenant_id = @t", tenant));
    }

    [Fact]
    [Trait("Requirement", "AT-BIL-02")]
    [Trait("Requirement", "BIL-005")]
    [Trait("Requirement", "BIL-007")]
    public async Task An_old_failure_cannot_regress_a_paid_renewal_and_retries_do_not_extend_grace()
    {
        var t = await ActiveAsync("wh-renew");
        var subRef = (string)(await ScalarAsync("SELECT provider_subscription_ref FROM cp.subscription WHERE tenant_id = @t", t.TenantId))!;
        var periodEnd = At(await ScalarAsync("SELECT period_end FROM cp.subscription WHERE tenant_id = @t", t.TenantId));

        var fail1 = Simulator.FailRenewal(subRef);
        var fail2 = Simulator.FailRenewal(subRef);
        var stale = Simulator.FailRenewal(subRef);
        var paid = Simulator.PayRenewal(subRef);

        await DeliverAndProcessAsync(fail1);
        Assert.Equal("PastDue", await ScalarAsync("SELECT state::text FROM cp.subscription WHERE tenant_id = @t", t.TenantId));
        var grace = At(await ScalarAsync("SELECT grace_end FROM cp.subscription WHERE tenant_id = @t", t.TenantId));
        Assert.Equal(periodEnd.AddDays(7), grace);

        await DeliverAndProcessAsync(fail2);
        Assert.Equal(grace, At(await ScalarAsync("SELECT grace_end FROM cp.subscription WHERE tenant_id = @t", t.TenantId)));
        // Still within grace: normal access with a billing banner, not restricted.
        var sub = await (await t.Owner.GetAsync("/api/v1/saas/subscription")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("PastDue", (string?)sub["state"]);

        await DeliverAndProcessAsync(paid);
        await DeliverAndProcessAsync(stale); // built before the payment, delivered after it
        Assert.Equal("Active", await ScalarAsync("SELECT state::text FROM cp.subscription WHERE tenant_id = @t", t.TenantId));
        Assert.Null(await ScalarAsync("SELECT grace_end FROM cp.subscription WHERE tenant_id = @t", t.TenantId));
        Assert.Equal(periodEnd.AddMonths(1), At(await ScalarAsync("SELECT period_end FROM cp.subscription WHERE tenant_id = @t", t.TenantId)));
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM cp.invoice WHERE tenant_id = @t AND status = 'Paid'", t.TenantId));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM cp.invoice WHERE tenant_id = @t AND status <> 'Paid'", t.TenantId));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'SubscriptionPastDue'", t.TenantId));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'PaymentRecovered'", t.TenantId));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM cp.billing_notice WHERE tenant_id = @t AND kind = 'PaymentFailed'", t.TenantId));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM cp.billing_notice WHERE tenant_id = @t AND kind = 'PaymentRecovered'", t.TenantId));
    }

    [Fact]
    [Trait("Requirement", "BIL-003")]
    public async Task A_declined_checkout_leaves_the_trial_and_the_session_as_they_were()
    {
        var t = await CheckoutAsync("wh-declined");
        await DeliverAndProcessAsync(Simulator.FailCheckout(t.CheckoutId));
        Assert.Equal("Trialing", await ScalarAsync("SELECT state::text FROM cp.subscription WHERE tenant_id = @t", t.TenantId));
        Assert.Equal("Open", await ScalarAsync("SELECT status::text FROM cp.checkout_session WHERE id = @id", null, ("id", t.CheckoutId)));
        Assert.Equal("Failed", await ScalarAsync("SELECT p.status::text FROM cp.payment p WHERE p.tenant_id = @t", t.TenantId));
        Assert.Equal(1L, await CountAsync("SELECT max(entitlement_version) FROM cp.entitlement_snapshot WHERE tenant_id = @t", t.TenantId));
    }

    [Fact]
    [Trait("Requirement", "BIL-008")]
    public async Task Disputes_chargebacks_and_refunds_open_one_operator_task_each_and_leave_access_alone()
    {
        var t = await ActiveAsync("wh-dispute");
        var paymentRef = (string)(await ScalarAsync("SELECT provider_payment_ref FROM cp.payment WHERE tenant_id = @t", t.TenantId))!;

        var dispute = Simulator.Dispute(paymentRef);
        await DeliverAndProcessAsync(dispute);
        await DeliverAndProcessAsync(Simulator.Resend(dispute));
        await DeliverAndProcessAsync(Simulator.Dispute(paymentRef, fundsWithdrawn: true));
        await DeliverAndProcessAsync(Simulator.Refund(paymentRef));

        foreach (var kind in new[] { "Dispute", "Chargeback", "Refund" })
            Assert.Equal(1L, await CountAsync("SELECT count(*) FROM cp.billing_task WHERE tenant_id = @t AND kind = @k::cp.billing_task_kind AND status = 'Open'",
                t.TenantId, ("k", kind)));
        Assert.Equal(1L, await CountAsync("""
            SELECT count(*) FROM cp.payment r JOIN cp.payment o ON o.id = r.refund_of_id WHERE r.tenant_id = @t AND r.status = 'Refunded'
            """, t.TenantId));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'BillingDisputeOpened'", t.TenantId));
        Assert.Equal("Active", await ScalarAsync("SELECT state::text FROM cp.subscription WHERE tenant_id = @t", t.TenantId));
    }

    [Fact]
    [Trait("Requirement", "AT-BIL-04")]
    [Trait("Requirement", "LFC-001")]
    public async Task A_payment_during_a_security_suspension_settles_billing_but_keeps_the_suspension()
    {
        var t = await CheckoutAsync("wh-suspended");
        await ExecAsync("""
            UPDATE ti.tenant SET security_suspended_at = now(), security_suspension_reason = 'Synthetic security review', security_suspended_by = 'user-platform-admin'
            WHERE id = @t
            """, ("t", t.TenantId));
        await DeliverAndProcessAsync(Simulator.PayCheckout(t.CheckoutId));

        Assert.Equal("Active", await ScalarAsync("SELECT state::text FROM cp.subscription WHERE tenant_id = @t", t.TenantId));
        Assert.NotNull(await ScalarAsync("SELECT security_suspended_at FROM ti.tenant WHERE id = @t", t.TenantId));
        var state = await fx.Factory.Services.GetRequiredService<ITenantAccessReader>().ReadAsync(t.TenantId, CancellationToken.None);
        Assert.Equal(AccessMode.SecuritySuspended, state!.Mode(DateTimeOffset.UtcNow));
        Assert.Null(state.BillingReason(DateTimeOffset.UtcNow));
    }

    [Fact]
    [Trait("Requirement", "AT-BIL-04")]
    [Trait("Requirement", "LFC-001")]
    public async Task A_payment_after_deletion_never_recreates_or_reactivates_the_workspace()
    {
        var t = await CheckoutAsync("wh-deleted");
        await ExecAsync("UPDATE ti.tenant SET lifecycle = 'Deleted', deletion_requested_at = now() - interval '1 day', deleted_at = now() WHERE id = @t",
            ("t", t.TenantId));
        var paid = Simulator.PayCheckout(t.CheckoutId);
        await DeliverAndProcessAsync(paid);

        Assert.Equal("Ignored", await ScalarAsync("SELECT processing_state::text FROM cp.webhook_inbox WHERE event_id = @e", null, ("e", paid.EventId)));
        Assert.Equal("TENANT_DELETED", await ScalarAsync("SELECT failure_code FROM cp.webhook_inbox WHERE event_id = @e", null, ("e", paid.EventId)));
        Assert.Equal("Deleted", await ScalarAsync("SELECT lifecycle::text FROM ti.tenant WHERE id = @t", t.TenantId));
        Assert.Equal("Trialing", await ScalarAsync("SELECT state::text FROM cp.subscription WHERE tenant_id = @t", t.TenantId));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM cp.invoice WHERE tenant_id = @t", t.TenantId));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM cp.billing_task WHERE tenant_id = @t AND kind = 'SubscriptionMismatch'", t.TenantId));
    }

    [Fact]
    [Trait("Requirement", "BIL-005")]
    public async Task A_processing_failure_rolls_back_and_retries_with_backoff()
    {
        var t = await CheckoutAsync("wh-retry");
        var paid = Simulator.PayCheckout(t.CheckoutId);
        await (await DeliverAsync(paid)).ExpectAsync(HttpStatusCode.OK);
        // Damage the stored ciphertext: decryption fails, so nothing may be applied.
        await ExecAsync("UPDATE cp.webhook_inbox SET payload_encrypted = overlay(payload_encrypted placing '\\x00'::bytea from 40 for 1) WHERE event_id = @e",
            ("e", paid.EventId));
        await ProcessAsync();

        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT processing_state::text, attempts, failure_code, last_error, next_attempt_at > now() + interval '20 seconds' FROM cp.webhook_inbox WHERE event_id = @e", conn);
        cmd.Parameters.AddWithValue("e", paid.EventId);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal("Failed", r.GetString(0));
        Assert.Equal(1, r.GetInt32(1));
        Assert.Equal("PROCESSING_ERROR", r.GetString(2));
        Assert.Equal("AuthenticationTagMismatchException", r.GetString(3));
        Assert.True(r.GetBoolean(4));
        Assert.Equal("Trialing", await ScalarAsync("SELECT state::text FROM cp.subscription WHERE tenant_id = @t", t.TenantId));
    }

    // ---- Helpers ----

    private sealed record Arranged(HttpClient Owner, Guid TenantId, Guid PriceId, Guid CheckoutId);

    private async Task<Arranged> CheckoutAsync(string label)
    {
        var tenant = await TenantProvisioning.SignUpAsync(fx, label);
        var owner = fx.Client(tenant.Admin, tenant.TenantId);
        var price = await CheckoutTests.PublishedPriceAsync(fx, 2500);
        var digest = (string)(await (await owner.PostJsonAsync("/api/v1/saas/checkout/preview", new { priceVersionId = price })).ExpectAsync(HttpStatusCode.OK))["previewDigest"]!;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saas/checkout")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { priceVersionId = price, acceptedPreviewDigest = digest }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var session = await (await owner.SendAsync(request)).ExpectAsync(HttpStatusCode.Created);
        return new Arranged(owner, tenant.TenantId, price, Guid.Parse((string)session["checkoutId"]!));
    }

    private async Task<Arranged> ActiveAsync(string label)
    {
        var t = await CheckoutAsync(label);
        await DeliverAndProcessAsync(Simulator.PayCheckout(t.CheckoutId));
        Assert.Equal("Active", await ScalarAsync("SELECT state::text FROM cp.subscription WHERE tenant_id = @t", t.TenantId));
        return t;
    }

    private Task<HttpResponseMessage> DeliverAsync(SimEvent e, string provider = "simulator")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, provider == "simulator" ? Url : $"/api/v1/webhooks/billing/{provider}")
        {
            Content = new ByteArrayContent(e.Body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (e.Signature.Length > 0)
            request.Headers.TryAddWithoutValidation(SimulatedBillingProvider.SignatureHeader, e.Signature);
        return fx.Anonymous().SendAsync(request);
    }

    private async Task DeliverAndProcessAsync(SimEvent e)
    {
        await (await DeliverAsync(e)).ExpectAsync(HttpStatusCode.OK);
        await ProcessAsync();
    }

    private Task ProcessAsync() => fx.Factory.Services.GetRequiredService<BillingEventJob>().RunOnceAsync(CancellationToken.None);

    private async Task RelayNoticesAsync()
    {
        await using var app = DependencyInjection.BuildDataSource(fx.AppConnection);
        await using var platform = DependencyInjection.BuildDataSource(fx.PlatformConnection);
        var runner = new TenantJobRunner(new PlatformDataSource(platform), new AppDataSource(app), new FakeTimeProvider(DateTimeOffset.UtcNow));
        await new BillingNoticeRelayJob(runner, NullLogger<BillingNoticeRelayJob>.Instance).RunOnceAsync(CancellationToken.None);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        return conn;
    }

    private async Task ExecAsync(string sql, params (string Name, object Value)[] args) => await TenantProvisioning.ExecAsSuperuserAsync(fx, sql, args);

    private Task<object?> ScalarAsync(string sql, Guid? tenantId, params (string Name, object Value)[] args) =>
        Scalar(sql, tenantId, args);

    private Task<object?> ScalarAsync(string sql, Guid tenantId) => Scalar(sql, tenantId, []);

    private async Task<object?> Scalar(string sql, Guid? tenantId, (string Name, object Value)[] args)
    {
        var all = tenantId is { } t ? args.Append(("t", (object)t)).ToArray() : args;
        var result = await TenantProvisioning.ScalarAsSuperuserAsync(fx, sql, all);
        return result is DBNull ? null : result;
    }

    private static DateTimeOffset At(object? value) => new(DateTime.SpecifyKind((DateTime)value!, DateTimeKind.Utc));

    private async Task<long> CountAsync(string sql, Guid? tenantId, params (string Name, object Value)[] args) =>
        Convert.ToInt64(await Scalar(sql, tenantId, args), System.Globalization.CultureInfo.InvariantCulture);
}
