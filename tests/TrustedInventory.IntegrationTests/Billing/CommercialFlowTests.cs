using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Infrastructure;
using TrustedInventory.Infrastructure.Billing;
using TrustedInventory.Infrastructure.Jobs;
using TrustedInventory.Infrastructure.Persistence;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Billing;

/// <summary>
/// S2 gate flow (DEC-062..066): one workspace through trial, consented checkout, verified activation, an upgrade at the next cycle,
/// a failed renewal with grace and reminders, recovery, cancellation at period end, restriction, and a new checkout, against the
/// labelled simulator. Ends with a clean reconciliation and an intact audit chain. Nothing can charge.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class CommercialFlowTests(ApiFixture fx)
{
    private static readonly string[] Core = ["core_inventory"];
    private static readonly string[] Growth = ["core_inventory", "service_api", "advanced_reporting"];

    private SimulatedBillingProvider Simulator => fx.Factory.Services.GetRequiredService<SimulatedBillingProvider>();

    [Fact]
    [Trait("Requirement", "AT-BIL-01")]
    [Trait("Requirement", "BIL-002")]
    [Trait("Requirement", "BIL-003")]
    [Trait("Requirement", "ENT-001")]
    [Trait("Requirement", "ENT-002")]
    [Trait("Requirement", "ENT-005")]
    [Trait("Requirement", "BIL-007")]
    [Trait("Requirement", "BIL-008")]
    [Trait("Requirement", "BIL-006")]
    public async Task A_workspace_moves_through_the_whole_commercial_lifecycle_on_the_simulator()
    {
        var signup = await TenantProvisioning.SignUpAsync(fx, "s2-flow");
        var owner = fx.Client(signup.Admin, signup.TenantId);
        var tenant = signup.TenantId;
        var starter = await CheckoutTests.PublishedPriceAsync(fx, 2500, features: Core, limitEach: 10);
        var growth = await CheckoutTests.PublishedPriceAsync(fx, 9900, features: Growth, limitEach: 100);
        Assert.Equal("Trialing", (string?)(await SubscriptionAsync(owner))["state"]);

        // 1. Consent, hosted checkout, and a return that proves nothing (BIL-002, BIL-003, AT-BIL-01).
        var checkout = await CheckoutAsync(owner, starter);
        Assert.StartsWith("https://billing-simulator.invalid/", (string?)checkout["hostedUrl"]);
        Assert.Equal("Trialing", (string?)(await SubscriptionAsync(owner))["state"]);

        // 2. The verified paid event activates once; the notice reaches the tenant outbox.
        var paid = Simulator.PayCheckout(Guid.Parse((string)checkout["checkoutId"]!));
        await DeliverAndProcessAsync(paid);
        await DeliverAndProcessAsync(Simulator.Resend(paid));
        var sub = await SubscriptionAsync(owner);
        Assert.Equal("Active", (string?)sub["state"]);
        Assert.Equal(starter.ToString(), (string?)sub["priceVersionId"]);
        Assert.Equal(10L, (long)sub["limits"]!["active_warehouses"]!);
        await RelayAsync();
        Assert.Equal(1L, await CountAsync("SELECT count(DISTINCT payload->>'noticeId') FROM ti.outbox_event WHERE tenant_id = @t AND event_type = 'BillingSubscriptionActivated'", tenant));
        await (await owner.PostJsonAsync("/api/v1/warehouses", new { code = "S2-1", name = "Main", timeZone = "UTC" })).ExpectAsync(HttpStatusCode.Created);
        var subscriptionRef = (string)(await ScalarAsync("SELECT provider_subscription_ref FROM cp.subscription WHERE tenant_id = @t", tenant))!;

        // 3. An upgrade is scheduled for the period end and applies once its first period is paid (ENT-005); the old price stays
        //    on its invoice (ENT-001).
        var periodEnd = await PeriodEndAsync(tenant);
        var preview = await (await owner.PostJsonAsync("/api/v1/saas/subscription/change/preview", new { targetPriceVersionId = growth }))
            .ExpectAsync(HttpStatusCode.OK);
        var change = await (await ChangeAsync(owner, growth, (string)preview["previewDigest"]!, (long)sub["version"]!)).ExpectAsync(HttpStatusCode.Accepted);
        await DeliverAndProcessAsync(Simulator.PayRenewal(subscriptionRef));
        Assert.Equal(SubscriptionChangeState.Applied, await ApplyChangeAsync(Guid.Parse((string)change["id"]!), periodEnd.AddMinutes(1)));
        sub = await SubscriptionAsync(owner);
        Assert.Equal(growth.ToString(), (string?)sub["priceVersionId"]);
        Assert.Equal(100L, (long)sub["limits"]!["active_warehouses"]!);
        var invoices = (await (await owner.GetAsync("/api/v1/saas/invoices")).ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray();
        Assert.Equal([2500L, 9900L], invoices.Select(i => (long)i!["total"]!["minorUnits"]!).Order());
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM cp.entitlement_snapshot WHERE tenant_id = @t AND is_current", tenant));

        // 4. A failed renewal: PastDue with one grace period, normal access within it, reminders on day 3, then recovery (BIL-007).
        await DeliverAndProcessAsync(Simulator.FailRenewal(subscriptionRef));
        sub = await SubscriptionAsync(owner);
        Assert.Equal("PastDue", (string?)sub["state"]);
        Assert.Equal("Normal", (string?)sub["accessMode"]);
        var graceEnd = DateTimeOffset.Parse((string)sub["graceEnd"]!, System.Globalization.CultureInfo.InvariantCulture);
        await new BillingReminderJob(fx.Factory.Services.GetRequiredService<ControlDataSource>(), new FakeTimeProvider(graceEnd.AddDays(-4).AddHours(1)))
            .RunOnceAsync(CancellationToken.None);
        await DeliverAndProcessAsync(Simulator.PayRenewal(subscriptionRef));
        Assert.Equal("Active", (string?)(await SubscriptionAsync(owner))["state"]);
        await RelayAsync();
        foreach (var notice in new[] { "BillingPaymentFailed", "BillingPaymentReminder", "BillingPaymentRecovered", "BillingPlanChangeApplied" })
            Assert.Equal(1L, await CountAsync("SELECT count(DISTINCT payload->>'noticeId') FROM ti.outbox_event WHERE tenant_id = @t AND event_type = @k", tenant, ("k", notice)));

        // 5. Cancellation at period end; once the provider ends it, routine writes stop but data and billing recovery remain (BIL-008).
        sub = await (await owner.PostJsonAsync("/api/v1/saas/subscription/cancel", new { reason = "End of the pilot" }, $"\"{(long)(await SubscriptionAsync(owner))["version"]!}\""))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.True((bool)sub["cancelAtPeriodEnd"]!);
        await DeliverAndProcessAsync(Simulator.EndSubscription(subscriptionRef));
        Assert.Equal("Active", (string?)(await SubscriptionAsync(owner))["state"]); // paid until period end (DEC-068)
        await new SubscriptionPeriodEndJob(fx.Factory.Services.GetRequiredService<ControlDataSource>(), new FakeTimeProvider(await PeriodEndAsync(tenant)))
            .EndDueAsync(CancellationToken.None);
        sub = await SubscriptionAsync(owner);
        Assert.Equal("Cancelled", (string?)sub["state"]);
        Assert.Equal("BillingRestricted", (string?)sub["accessMode"]);
        await (await owner.PostJsonAsync("/api/v1/warehouses", new { code = "S2-2", name = "After end", timeZone = "UTC" }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "SUBSCRIPTION_RESTRICTED");
        Assert.Single((await (await owner.GetAsync("/api/v1/warehouses")).ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray());

        // 6. A new consented checkout starts a new paid subscription on the same workspace.
        var again = await CheckoutAsync(owner, starter);
        await DeliverAndProcessAsync(Simulator.PayCheckout(Guid.Parse((string)again["checkoutId"]!)));
        sub = await SubscriptionAsync(owner);
        Assert.Equal("Active", (string?)sub["state"]);
        Assert.False((bool)sub["cancelAtPeriodEnd"]!);
        Assert.NotEqual(subscriptionRef, (string?)await ScalarAsync("SELECT provider_subscription_ref FROM cp.subscription WHERE tenant_id = @t", tenant));
        await (await owner.PostJsonAsync("/api/v1/warehouses", new { code = "S2-3", name = "Back", timeZone = "UTC" })).ExpectAsync(HttpStatusCode.Created);

        // 7. Reconciliation agrees with the provider, and every billing step is in the tenant's intact audit chain (BIL-006, AUD-001).
        var reconciled = await new BillingReconciliationJob(fx.Factory.Services.GetRequiredService<ControlDataSource>(), Simulator,
            fx.Factory.Services.GetRequiredService<WebhookInbox>(), fx.Factory.Services.GetRequiredService<BillingEventJob>(), TimeProvider.System,
            NullLogger<BillingReconciliationJob>.Instance).RunAsync(CancellationToken.None);
        Assert.True(reconciled.ProviderReachable);
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM cp.billing_task WHERE tenant_id = @t AND kind IN ('SubscriptionMismatch','EntitlementMismatch')", tenant));
        await TenantProvisioning.GrantSelfAsync(fx, signup, "Auditor"); // the owner does not read the audit trail by default
        Assert.True((bool)(await (await owner.PostJsonAsync("/api/v1/audit/verify", new { })).ExpectAsync(HttpStatusCode.OK))["verified"]!);
        foreach (var type in new[] { "CheckoutStarted", "CheckoutCompleted", "SubscriptionActivated", "SubscriptionChangeRequested", "SubscriptionChangeApplied",
                     "SubscriptionPastDue", "PaymentRecovered", "SubscriptionCancellationRequested", "SubscriptionEnded" })
            Assert.True(await CountAsync("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = @e", tenant, ("e", type)) >= 1, type);
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'SubscriptionActivated'", tenant));
    }

    [Theory]
    [Trait("Requirement", "BIL-002")]
    [Trait("Requirement", "AGT-004")]
    [InlineData("Billing:Provider", "stripe")]
    [InlineData("Billing:LiveCheckoutEnabled", "true")]
    [InlineData("Billing:Simulator:SigningSecret", "too-short")]
    [InlineData("Billing:Simulator:SigningSecret", "")]
    [InlineData("Billing:InboxKey", "bm90LTMyLWJ5dGVz")]
    [InlineData("Billing:InboxKey", "not base64!")]
    public void Startup_refuses_any_live_provider_live_checkout_or_missing_billing_secrets(string key, string value)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Billing:Provider"] = "simulator",
            ["Billing:Simulator:SigningSecret"] = ApiFixture.SimulatorSecret,
            ["Billing:InboxKey"] = Convert.ToBase64String(ApiFixture.InboxKey),
        };
        new ServiceCollection().AddBilling(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()); // the valid baseline starts
        settings[key] = value;
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddBilling(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
    }

    // ---- Helpers ----

    private static async Task<JsonObject> SubscriptionAsync(HttpClient owner) =>
        await (await owner.GetAsync("/api/v1/saas/subscription")).ExpectAsync(HttpStatusCode.OK);

    private static async Task<JsonObject> CheckoutAsync(HttpClient owner, Guid price)
    {
        var digest = (string)(await (await owner.PostJsonAsync("/api/v1/saas/checkout/preview", new { priceVersionId = price })).ExpectAsync(HttpStatusCode.OK))["previewDigest"]!;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saas/checkout")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { priceVersionId = price, acceptedPreviewDigest = digest }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await (await owner.SendAsync(request)).ExpectAsync(HttpStatusCode.Created);
    }

    private static Task<HttpResponseMessage> ChangeAsync(HttpClient owner, Guid price, string digest, long ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saas/subscription/change")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { targetPriceVersionId = price, acceptedPreviewDigest = digest }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{ifMatch}\""));
        return owner.SendAsync(request);
    }

    private async Task DeliverAndProcessAsync(SimEvent e)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/billing/simulator") { Content = new ByteArrayContent(e.Body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(SimulatedBillingProvider.SignatureHeader, e.Signature);
        await (await fx.Anonymous().SendAsync(request)).ExpectAsync(HttpStatusCode.OK);
        await fx.Factory.Services.GetRequiredService<BillingEventJob>().RunOnceAsync(CancellationToken.None);
    }

    private async Task<SubscriptionChangeState> ApplyChangeAsync(Guid changeId, DateTimeOffset at)
    {
        await using var app = DependencyInjection.BuildDataSource(fx.AppConnection);
        await using var platform = DependencyInjection.BuildDataSource(fx.PlatformConnection);
        var clock = new FakeTimeProvider(at);
        var runner = new TenantJobRunner(new PlatformDataSource(platform), new AppDataSource(app), clock);
        return await new PlanChangeJob(fx.Factory.Services.GetRequiredService<ControlDataSource>(), runner, Simulator, clock,
            NullLogger<PlanChangeJob>.Instance).ApplyAsync(changeId, CancellationToken.None);
    }

    private async Task RelayAsync()
    {
        await using var app = DependencyInjection.BuildDataSource(fx.AppConnection);
        await using var platform = DependencyInjection.BuildDataSource(fx.PlatformConnection);
        var runner = new TenantJobRunner(new PlatformDataSource(platform), new AppDataSource(app), new FakeTimeProvider(DateTimeOffset.UtcNow));
        await new BillingNoticeRelayJob(runner, NullLogger<BillingNoticeRelayJob>.Instance).RunOnceAsync(CancellationToken.None);
    }

    private async Task<DateTimeOffset> PeriodEndAsync(Guid tenant) =>
        new(DateTime.SpecifyKind((DateTime)(await ScalarAsync("SELECT period_end FROM cp.subscription WHERE tenant_id = @t", tenant))!, DateTimeKind.Utc));

    private async Task<object?> ScalarAsync(string sql, Guid tenant, params (string Name, object Value)[] args)
    {
        var result = await TenantProvisioning.ScalarAsSuperuserAsync(fx, sql, args.Append(("t", (object)tenant)).ToArray());
        return result is DBNull ? null : result;
    }

    private async Task<long> CountAsync(string sql, Guid tenant, params (string Name, object Value)[] args) =>
        Convert.ToInt64(await ScalarAsync(sql, tenant, args), System.Globalization.CultureInfo.InvariantCulture);
}
