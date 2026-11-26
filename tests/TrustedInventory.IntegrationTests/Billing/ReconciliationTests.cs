using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TrustedInventory.Infrastructure.Billing;
using TrustedInventory.Infrastructure.Persistence;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Billing;

/// <summary>Reconciliation, provider outage, payment reminders, backlog signal and the platform billing consoles (DEC-066).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ReconciliationTests(ApiFixture fx)
{
    private static readonly string[] CoreOnly = ["core_inventory"];

    private SimulatedBillingProvider Simulator => fx.Factory.Services.GetRequiredService<SimulatedBillingProvider>();

    [Fact]
    [Trait("Requirement", "AT-SOP-01")]
    [Trait("Requirement", "BIL-005")]
    [Trait("Requirement", "BIL-006")]
    public async Task A_provider_outage_keeps_the_last_verified_access_and_reconciliation_converges_after_recovery()
    {
        var t = await ActiveAsync("rc-outage");
        Simulator.Outage = true;
        ReconciliationResult down;
        try
        {
            down = await ReconcileAsync();
            // Tenants keep working on their last verified state; webhooks are still stored.
            await (await t.Owner.PostJsonAsync("/api/v1/warehouses", new { code = "OUT-1", name = "During outage", timeZone = "UTC" }))
                .ExpectAsync(HttpStatusCode.Created);
            Assert.Equal("Normal", (string?)(await (await t.Owner.GetAsync("/api/v1/saas/subscription")).ExpectAsync(HttpStatusCode.OK))["accessMode"]);
            await (await DeliverAsync(Simulator.Build("customer.updated", new SimEventData(t.CustomerRef)))).ExpectAsync(HttpStatusCode.OK);
        }
        finally
        {
            Simulator.Outage = false;
        }
        Assert.False(down.ProviderReachable);
        Assert.Equal("PROVIDER_UNAVAILABLE", await ScalarAsync("SELECT last_error FROM cp.billing_sync_state WHERE provider = 'simulator'", null));
        Assert.Equal("Active", await ScalarAsync("SELECT state::text FROM cp.subscription WHERE tenant_id = @t", t.TenantId));

        // A renewal failure whose webhook never arrived is picked up from the provider's event log and applied by the normal path.
        var missed = Simulator.FailRenewal(t.SubscriptionRef);
        var up = await ReconcileAsync();
        Assert.True(up.ProviderReachable);
        Assert.True(up.EventsFetched >= 1);
        Assert.Equal("PastDue", await ScalarAsync("SELECT state::text FROM cp.subscription WHERE tenant_id = @t", t.TenantId));
        Assert.Equal("Reconciliation", await ScalarAsync("SELECT received_via FROM cp.webhook_inbox WHERE event_id = @e", null, ("e", missed.EventId)));
        Assert.Null(await ScalarAsync("SELECT last_error FROM cp.billing_sync_state WHERE provider = 'simulator'", null));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM cp.billing_task WHERE tenant_id = @t AND kind = 'SubscriptionMismatch'", t.TenantId));

        // The late webhook is now a duplicate, and a second run fetches nothing new for it.
        Assert.True((bool)(await (await DeliverAsync(missed)).ExpectAsync(HttpStatusCode.OK))["duplicate"]!);
        await ReconcileAsync();
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM cp.webhook_inbox WHERE event_id = @e", null, ("e", missed.EventId)));
    }

    [Fact]
    [Trait("Requirement", "BIL-006")]
    [Trait("Requirement", "SOP-003")]
    public async Task Reconciliation_opens_one_task_per_difference_unpaid_invoice_and_entitlement_mismatch()
    {
        var drifted = await ActiveAsync("rc-drift");
        await ExecAsync("UPDATE cp.subscription SET period_end = period_end + interval '1 day' WHERE tenant_id = @t", ("t", drifted.TenantId));
        await ExecAsync("""
            UPDATE cp.entitlement_snapshot SET plan_version_id = (SELECT id FROM cp.plan_version WHERE id <> e.plan_version_id LIMIT 1)
            FROM cp.entitlement_snapshot e WHERE e.id = cp.entitlement_snapshot.id AND e.tenant_id = @t AND e.is_current
            """, ("t", drifted.TenantId));

        var owing = await ActiveAsync("rc-owing");
        await DeliverAndProcessAsync(Simulator.FailRenewal(owing.SubscriptionRef));
        await ExecAsync("UPDATE cp.invoice SET due_at = now() - interval '1 day' WHERE tenant_id = @t AND status = 'Open'", ("t", owing.TenantId));

        for (var run = 0; run < 2; run++)
            Assert.True((await ReconcileAsync()).ProviderReachable);

        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM cp.billing_task WHERE tenant_id = @t AND kind = 'SubscriptionMismatch'", drifted.TenantId));
        Assert.Contains("period end", (string?)await ScalarAsync("SELECT detail FROM cp.billing_task WHERE tenant_id = @t AND kind = 'SubscriptionMismatch'",
            drifted.TenantId));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM cp.billing_task WHERE tenant_id = @t AND kind = 'EntitlementMismatch'", drifted.TenantId));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM cp.billing_task WHERE tenant_id = @t AND kind = 'UnpaidInvoice'", owing.TenantId));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM cp.billing_task WHERE tenant_id = @t AND kind = 'SubscriptionMismatch'", owing.TenantId));
        // Nothing is corrected silently: our records still show the drift.
        Assert.Equal("Active", await ScalarAsync("SELECT state::text FROM cp.subscription WHERE tenant_id = @t", drifted.TenantId));
    }

    [Fact]
    [Trait("Requirement", "BIL-007")]
    public async Task A_past_due_subscription_gets_one_reminder_on_day_3_and_one_on_day_6()
    {
        var t = await ActiveAsync("rc-remind");
        await DeliverAndProcessAsync(Simulator.FailRenewal(t.SubscriptionRef));
        var graceEnd = At(await ScalarAsync("SELECT grace_end FROM cp.subscription WHERE tenant_id = @t", t.TenantId));
        var due = graceEnd.AddDays(-7);

        async Task<long> RemindersAt(DateTimeOffset at)
        {
            await new BillingReminderJob(fx.Factory.Services.GetRequiredService<ControlDataSource>(), new FakeTimeProvider(at)).RunOnceAsync(CancellationToken.None);
            return await CountAsync("SELECT count(*) FROM cp.billing_notice WHERE tenant_id = @t AND kind = 'PaymentReminder'", t.TenantId);
        }

        Assert.Equal(0L, await RemindersAt(due.AddDays(2)));
        Assert.Equal(1L, await RemindersAt(due.AddDays(3).AddHours(1)));
        Assert.Equal(1L, await RemindersAt(due.AddDays(3).AddHours(5)));
        Assert.Equal(2L, await RemindersAt(due.AddDays(6).AddHours(1)));
        Assert.Equal(2L, await RemindersAt(graceEnd.AddHours(1)));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM cp.billing_notice WHERE tenant_id = @t AND kind = 'PaymentFailed'", t.TenantId));
    }

    [Fact]
    [Trait("Requirement", "AT-BIL-03")]
    [Trait("Requirement", "BIL-007")]
    public async Task Writes_stop_at_the_grace_end_of_a_real_failed_renewal_without_any_worker()
    {
        var t = await ActiveAsync("rc-grace");
        await DeliverAndProcessAsync(Simulator.FailRenewal(t.SubscriptionRef));
        // Bring the grace end within minutes of real time so token validation is unaffected (as StateMatrixTests does for trials).
        var start = DateTimeOffset.UtcNow;
        start = start.AddTicks(-(start.Ticks % 10));
        var graceEnd = start.AddMinutes(2);
        await ExecAsync("UPDATE cp.subscription SET grace_end = @g WHERE tenant_id = @t", ("g", graceEnd), ("t", t.TenantId));
        var clock = new FakeTimeProvider(start);
        await using var factory = fx.Factory.WithWebHostBuilder(web =>
            web.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock))));
        var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Authorization = t.Owner.DefaultRequestHeaders.Authorization;
        owner.DefaultRequestHeaders.Add("X-Tenant-Id", t.TenantId.ToString());

        clock.SetUtcNow(graceEnd.AddTicks(-10));
        await (await owner.PostJsonAsync("/api/v1/warehouses", new { code = "GR-1", name = "Within grace", timeZone = "UTC" })).ExpectAsync(HttpStatusCode.Created);
        clock.SetUtcNow(graceEnd);
        var problem = await (await owner.PostJsonAsync("/api/v1/warehouses", new { code = "GR-2", name = "Grace over", timeZone = "UTC" }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "SUBSCRIPTION_RESTRICTED");
        Assert.Contains("PaymentOverdue", problem.ToJsonString(), StringComparison.Ordinal);
        await (await owner.GetAsync("/api/v1/warehouses")).ExpectAsync(HttpStatusCode.OK);
        // Billing recovery stays open while restricted.
        await (await owner.GetAsync("/api/v1/saas/invoices")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("PastDue", await ScalarAsync("SELECT state::text FROM cp.subscription WHERE tenant_id = @t", t.TenantId));
    }

    [Fact]
    [Trait("Requirement", "SOP-003")]
    public async Task An_old_or_exhausted_inbox_event_shows_in_the_backlog_signal()
    {
        var old = Simulator.Build("customer.updated", new SimEventData("sim_cus_" + Guid.NewGuid().ToString("N")));
        var exhausted = Simulator.Build("customer.updated", new SimEventData("sim_cus_" + Guid.NewGuid().ToString("N")));
        await (await DeliverAsync(old)).ExpectAsync(HttpStatusCode.OK);
        await (await DeliverAsync(exhausted)).ExpectAsync(HttpStatusCode.OK);
        var oldAt = DateTimeOffset.UtcNow.AddHours(-1);
        await ExecAsync("UPDATE cp.webhook_inbox SET received_at = @at WHERE event_id = @e", ("at", oldAt), ("e", old.EventId));
        await ExecAsync("UPDATE cp.webhook_inbox SET processing_state = 'Failed', attempts = 10, failure_code = 'PROCESSING_ERROR' WHERE event_id = @e",
            ("e", exhausted.EventId));

        var backlog = await fx.Factory.Services.GetRequiredService<BillingEventJob>().BacklogAsync(CancellationToken.None);
        Assert.True(backlog.OldestPending <= oldAt.AddSeconds(1));
        Assert.True(backlog.Exhausted >= 1);
        await fx.Factory.Services.GetRequiredService<BillingEventJob>().RunOnceAsync(CancellationToken.None);
        Assert.Equal("Ignored", await ScalarAsync("SELECT processing_state::text FROM cp.webhook_inbox WHERE event_id = @e", null, ("e", old.EventId)));
        Assert.Equal("Failed", await ScalarAsync("SELECT processing_state::text FROM cp.webhook_inbox WHERE event_id = @e", null, ("e", exhausted.EventId)));
    }

    [Fact]
    [Trait("Requirement", "BIL-008")]
    [Trait("Requirement", "SOP-003")]
    [Trait("Requirement", "AT-BIL-02")]
    public async Task Platform_billing_operators_list_and_resolve_tasks_and_see_inbox_metadata_only()
    {
        var t = await ActiveAsync("rc-console");
        var paymentRef = (string)(await ScalarAsync("SELECT provider_payment_ref FROM cp.payment WHERE tenant_id = @t", t.TenantId))!;
        await DeliverAndProcessAsync(Simulator.Dispute(paymentRef));
        var unknown = Simulator.Build("customer.subscription.deleted", new SimEventData("sim_cus_" + Guid.NewGuid().ToString("N"), "sim_sub_x", 1));
        await DeliverAndProcessAsync(unknown);
        var resent = Simulator.Resend(unknown); // a second quarantined event, newest first
        await DeliverAndProcessAsync(resent);

        var billing = fx.Client(SeedData.PlatformBilling, tenantId: null);
        var tasks = await (await billing.GetAsync("/platform/v1/billing/reconciliation-tasks?status=Open&kind=Dispute&limit=200")).ExpectAsync(HttpStatusCode.OK);
        var task = tasks["items"]!.AsArray().Single(i => (string?)i!["tenantId"] == t.TenantId.ToString())!;
        Assert.Equal("Dispute", (string?)task["kind"]);
        Assert.All(tasks["items"]!.AsArray(), i => Assert.Equal("Open", (string?)i!["status"]));

        // Only PlatformBilling (SOD-8); tenant users are not platform callers at all.
        await (await fx.Client(SeedData.PlatformAdmin, tenantId: null).GetAsync("/platform/v1/billing/reconciliation-tasks"))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.True((await t.Owner.GetAsync("/platform/v1/billing/webhook-events")).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);

        var url = $"/platform/v1/billing/reconciliation-tasks/{task["id"]}/resolve";
        await (await billing.PostJsonAsync(url, new { resolution = " " })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        var resolved = await (await billing.PostJsonAsync(url, new { resolution = "Dispute evidence sent to the provider; access unchanged" })).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Resolved", (string?)resolved["status"]);
        Assert.Equal(SeedData.PlatformBilling, (string?)resolved["resolvedBy"]);
        await (await billing.PostJsonAsync(url, new { resolution = "again" })).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        await (await billing.PostJsonAsync($"/platform/v1/billing/reconciliation-tasks/{Guid.NewGuid()}/resolve", new { resolution = "x" }))
            .ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'BillingTaskResolved'", t.TenantId));
        Assert.Equal("Active", await ScalarAsync("SELECT state::text FROM cp.subscription WHERE tenant_id = @t", t.TenantId));

        // Inbox metadata for quarantine review, paged; no payload or summary fields.
        var events = await (await billing.GetAsync("/platform/v1/billing/webhook-events?state=Quarantined&limit=1")).ExpectAsync(HttpStatusCode.OK);
        var first = Assert.Single(events["items"]!.AsArray())!;
        Assert.Equal(["eventId", "eventType", "failureCode", "id", "processedAt", "provider", "providerEventAt", "receivedAt", "retries", "state"],
            first.AsObject().Select(p => p.Key).Order());
        Assert.Equal(resent.EventId, (string?)first["eventId"]);
        var next = (string)events["nextCursor"]!;
        var second = await (await billing.GetAsync($"/platform/v1/billing/webhook-events?state=Quarantined&limit=1&cursor={Uri.EscapeDataString(next)}"))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.NotEqual((string?)first["id"], (string?)second["items"]![0]!["id"]);
        await (await billing.GetAsync($"/platform/v1/billing/webhook-events?state=Failed&cursor={Uri.EscapeDataString(next)}"))
            .ExpectProblemAsync(HttpStatusCode.BadRequest, "INVALID_CURSOR");
    }

    // ---- Helpers ----

    private sealed record Arranged(HttpClient Owner, Guid TenantId, string CustomerRef, string SubscriptionRef);

    private async Task<Arranged> ActiveAsync(string label)
    {
        var tenant = await TenantProvisioning.SignUpAsync(fx, label);
        var owner = fx.Client(tenant.Admin, tenant.TenantId);
        var price = await CheckoutTests.PublishedPriceAsync(fx, 2500, features: CoreOnly, limitEach: 50);
        var digest = (string)(await (await owner.PostJsonAsync("/api/v1/saas/checkout/preview", new { priceVersionId = price })).ExpectAsync(HttpStatusCode.OK))["previewDigest"]!;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saas/checkout")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { priceVersionId = price, acceptedPreviewDigest = digest }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var session = await (await owner.SendAsync(request)).ExpectAsync(HttpStatusCode.Created);
        await DeliverAndProcessAsync(Simulator.PayCheckout(Guid.Parse((string)session["checkoutId"]!)));
        var customer = (string)(await ScalarAsync("SELECT provider_customer_id FROM cp.billing_account WHERE tenant_id = @t", tenant.TenantId))!;
        var subscription = (string)(await ScalarAsync("SELECT provider_subscription_ref FROM cp.subscription WHERE tenant_id = @t", tenant.TenantId))!;
        return new Arranged(owner, tenant.TenantId, customer, subscription);
    }

    private Task<ReconciliationResult> ReconcileAsync()
    {
        var services = fx.Factory.Services;
        return new BillingReconciliationJob(services.GetRequiredService<ControlDataSource>(), Simulator, services.GetRequiredService<WebhookInbox>(),
            services.GetRequiredService<BillingEventJob>(), TimeProvider.System, NullLogger<BillingReconciliationJob>.Instance).RunAsync(CancellationToken.None);
    }

    private Task<HttpResponseMessage> DeliverAsync(SimEvent e)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/billing/simulator") { Content = new ByteArrayContent(e.Body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(SimulatedBillingProvider.SignatureHeader, e.Signature);
        return fx.Anonymous().SendAsync(request);
    }

    private async Task DeliverAndProcessAsync(SimEvent e)
    {
        await (await DeliverAsync(e)).ExpectAsync(HttpStatusCode.OK);
        await fx.Factory.Services.GetRequiredService<BillingEventJob>().RunOnceAsync(CancellationToken.None);
    }

    private Task<int> ExecAsync(string sql, params (string Name, object Value)[] args) => TenantProvisioning.ExecAsSuperuserAsync(fx, sql, args);

    private async Task<object?> ScalarAsync(string sql, Guid? tenantId, params (string Name, object Value)[] args)
    {
        var all = tenantId is { } t ? args.Append(("t", (object)t)).ToArray() : args;
        var result = await TenantProvisioning.ScalarAsSuperuserAsync(fx, sql, all);
        return result is DBNull ? null : result;
    }

    private async Task<long> CountAsync(string sql, Guid? tenantId, params (string Name, object Value)[] args) =>
        Convert.ToInt64(await ScalarAsync(sql, tenantId, args), System.Globalization.CultureInfo.InvariantCulture);

    private static DateTimeOffset At(object? value) => new(DateTime.SpecifyKind((DateTime)value!, DateTimeKind.Utc));
}
