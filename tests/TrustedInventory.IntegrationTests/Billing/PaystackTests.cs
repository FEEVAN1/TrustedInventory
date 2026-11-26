using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TrustedInventory.Application.Billing;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Infrastructure;
using TrustedInventory.Infrastructure.Billing;
using TrustedInventory.Infrastructure.Jobs;
using TrustedInventory.Infrastructure.Persistence;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Billing;

/// <summary>
/// The Paystack adapter (owner decision D09, DEC-068) against the labelled Paystack API simulator: NGN prices, hosted payment,
/// HMAC-SHA512 webhooks, renewals, cancel and resume, and plan changes as subscription replacement. Test mode only; no network.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class PaystackTests(ApiFixture fx) : IDisposable
{
    private static readonly string[] Core = ["core_inventory"];
    private static readonly string[] Growth = ["core_inventory", "service_api", "advanced_reporting"];
    private const string Url = "/api/v1/webhooks/billing/paystack";

    private readonly SimulatedPaystackApi _api = new("sk_test_" + new string('5', 40));
    private WebApplicationFactory<Program>? _factory;

    private WebApplicationFactory<Program> Factory => _factory ??= Build();

    public void Dispose()
    {
        _factory?.Dispose();
        _api.Dispose();
    }

    [Fact]
    [Trait("Requirement", "BIL-002")]
    [Trait("Requirement", "BIL-003")]
    [Trait("Requirement", "BIL-001")]
    [Trait("Requirement", "AT-BIL-01")]
    public async Task A_Paystack_checkout_activates_only_from_a_verified_charge_with_the_period_Paystack_set()
    {
        var t = await TenantAsync("ps-pay");
        var price = await NgnPriceAsync(500000, Core, 10); // NGN 5,000.00 in kobo
        Assert.True((bool)(await ScalarAsync("SELECT display_approved FROM cp.price_version WHERE id = @p", null, ("p", price)))!);
        // A test-currency price is never offered through Paystack.
        var xts = await CheckoutTests.PublishedPriceAsync(fx, 2500);
        await (await t.Owner.PostJsonAsync("/api/v1/saas/checkout/preview", new { priceVersionId = xts })).ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");

        var preview = await (await t.Owner.PostJsonAsync("/api/v1/saas/checkout/preview", new { priceVersionId = price })).ExpectAsync(HttpStatusCode.OK);
        Assert.Contains("NGN 5000.00", (string?)preview["renewal"]);
        var session = await CheckoutAsync(t, price, (string)preview["previewDigest"]!);
        Assert.StartsWith("https://checkout.paystack-simulated.invalid/", (string?)session["hostedUrl"]);
        var reference = "ti-cs-" + Guid.Parse((string)session["checkoutId"]!).ToString("N");
        var tx = _api.Transaction(reference)!;
        Assert.StartsWith("PLN_", (string?)tx["plan"]);
        Assert.Equal(500000L, (long)tx["amount"]!);
        Assert.Equal("Trialing", (string?)(await SubscriptionAsync(t))["state"]);

        var paid = _api.Pay(reference);
        Assert.False((bool)(await (await DeliverAsync(paid)).ExpectAsync(HttpStatusCode.OK))["duplicate"]!);
        Assert.True((bool)(await (await DeliverAsync(paid)).ExpectAsync(HttpStatusCode.OK))["duplicate"]!);
        await ProcessAsync();

        var sub = await SubscriptionAsync(t);
        Assert.Equal("Active", (string?)sub["state"]);
        var paystackSub = _api.SubscriptionsOf((string)(await ScalarAsync("SELECT provider_customer_id FROM cp.billing_account WHERE tenant_id = @t", t.TenantId))!).Single();
        Assert.Equal((string?)paystackSub["subscription_code"], (string?)await ScalarAsync("SELECT provider_subscription_ref FROM cp.subscription WHERE tenant_id = @t", t.TenantId));
        Assert.Equal(DateTimeOffset.Parse((string)paystackSub["next_payment_date"]!, System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse((string)sub["periodEnd"]!, System.Globalization.CultureInfo.InvariantCulture));
        var invoice = (await (await t.Owner.GetAsync("/api/v1/saas/invoices")).ExpectAsync(HttpStatusCode.OK))["items"]![0]!;
        Assert.Equal("NGN", (string?)invoice["total"]!["currency"]);
        Assert.Equal(500000L, (long)invoice["total"]!["minorUnits"]!);
        Assert.Equal("Paid", (string?)invoice["status"]);
    }

    [Fact]
    [Trait("Requirement", "BIL-004")]
    [Trait("Requirement", "AT-BIL-02")]
    public async Task Paystack_webhooks_must_carry_an_HMAC_SHA512_of_the_exact_body_and_may_be_limited_to_Paystack_addresses()
    {
        var good = _api.Sign("customeridentification.success", new JsonObject { ["id"] = Random.Shared.NextInt64(1, long.MaxValue), ["customer_code"] = "CUS_x" });
        var forged = new SimulatedPaystackApi("sk_test_" + new string('9', 40)).Sign("customeridentification.success", new JsonObject { ["id"] = 1 });
        await (await DeliverAsync(forged)).ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await (await DeliverAsync(good with { Signature = "" })).ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        var tampered = good with { Body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(good.Body).Replace("CUS_x", "CUS_y", StringComparison.Ordinal)) };
        await (await DeliverAsync(tampered)).ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await (await DeliverAsync(good, "/api/v1/webhooks/billing/simulator")).ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        var malformed = _api.Sign("charge.success", new JsonObject { ["id"] = 7, ["reference"] = "ti-cs-x" });
        await (await DeliverAsync(malformed)).ExpectProblemAsync(HttpStatusCode.BadRequest, "MALFORMED_REQUEST");
        await (await DeliverAsync(good)).ExpectAsync(HttpStatusCode.OK);

        // With Paystack's source addresses configured, anything else is refused even when correctly signed.
        await using var restricted = Build(("Billing:Paystack:WebhookSourceIps", "52.31.139.75,52.49.173.169,52.214.14.220"));
        var request = Request(_api.Sign("customeridentification.success", new JsonObject { ["id"] = 99 }), Url);
        await (await restricted.CreateClient().SendAsync(request)).ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    [Trait("Requirement", "BIL-007")]
    [Trait("Requirement", "BIL-008")]
    public async Task Renewals_cancel_and_resume_drive_Paystack_and_a_cancelled_period_runs_to_its_end()
    {
        var t = await ActiveAsync("ps-renew", Core, 10);
        var code = (string)(await ScalarAsync("SELECT provider_subscription_ref FROM cp.subscription WHERE tenant_id = @t", t.TenantId))!;

        await DeliverAndProcessAsync(_api.Renew(code, success: false));
        Assert.Equal("PastDue", (string?)(await SubscriptionAsync(t))["state"]);
        await DeliverAndProcessAsync(_api.Renew(code, success: true));
        var sub = await SubscriptionAsync(t);
        Assert.Equal("Active", (string?)sub["state"]);
        Assert.Null(sub["graceEnd"]);

        // Cancel disables renewal at Paystack; Paystack's disable event does not end the paid period early.
        sub = await (await t.Owner.PostJsonAsync("/api/v1/saas/subscription/cancel", new { reason = "Pilot over" }, $"\"{(long)sub["version"]!}\""))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("cancelled", (string?)_api.Subscription(code)!["status"]);
        await DeliverAndProcessAsync(_api.Disabled(code));
        sub = await SubscriptionAsync(t);
        Assert.Equal("Active", (string?)sub["state"]);
        Assert.True((bool)sub["cancelAtPeriodEnd"]!);
        Assert.Equal("Normal", (string?)sub["accessMode"]);

        sub = await (await t.Owner.PostEmptyAsync("/api/v1/saas/subscription/resume", $"\"{(long)sub["version"]!}\"")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("active", (string?)_api.Subscription(code)!["status"]);
        Assert.False((bool)sub["cancelAtPeriodEnd"]!);

        sub = await (await t.Owner.PostJsonAsync("/api/v1/saas/subscription/cancel", new { reason = "Pilot over" }, $"\"{(long)sub["version"]!}\""))
            .ExpectAsync(HttpStatusCode.OK);
        var periodEnd = DateTimeOffset.Parse((string)sub["periodEnd"]!, System.Globalization.CultureInfo.InvariantCulture);
        var job = new SubscriptionPeriodEndJob(fx.Factory.Services.GetRequiredService<ControlDataSource>(), new FakeTimeProvider(periodEnd.AddMinutes(-1)));
        await job.EndDueAsync(CancellationToken.None);
        Assert.Equal("Active", await ScalarAsync("SELECT state::text FROM cp.subscription WHERE tenant_id = @t", t.TenantId));
        await new SubscriptionPeriodEndJob(fx.Factory.Services.GetRequiredService<ControlDataSource>(), new FakeTimeProvider(periodEnd)).EndDueAsync(CancellationToken.None);
        Assert.Equal("Cancelled", await ScalarAsync("SELECT state::text FROM cp.subscription WHERE tenant_id = @t", t.TenantId));
    }

    [Fact]
    [Trait("Requirement", "ENT-005")]
    [Trait("Requirement", "ENT-006")]
    public async Task A_plan_change_replaces_the_Paystack_subscription_from_the_period_end()
    {
        var t = await ActiveAsync("ps-change", Core, 10);
        var growth = await NgnPriceAsync(1500000, Growth, 100);
        var old = (string)(await ScalarAsync("SELECT provider_subscription_ref FROM cp.subscription WHERE tenant_id = @t", t.TenantId))!;
        var sub = await SubscriptionAsync(t);
        var periodEnd = DateTimeOffset.Parse((string)sub["periodEnd"]!, System.Globalization.CultureInfo.InvariantCulture);

        var preview = await (await t.Owner.PostJsonAsync("/api/v1/saas/subscription/change/preview", new { targetPriceVersionId = growth })).ExpectAsync(HttpStatusCode.OK);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saas/subscription/change")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { targetPriceVersionId = growth, acceptedPreviewDigest = (string)preview["previewDigest"]! }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{(long)sub["version"]!}\""));
        var change = await (await t.Owner.SendAsync(request)).ExpectAsync(HttpStatusCode.Accepted);

        var replacement = (string)(await ScalarAsync("SELECT provider_new_subscription_ref FROM cp.subscription_change WHERE id = @id", null,
            ("id", Guid.Parse((string)change["id"]!))))!;
        Assert.NotEqual(old, replacement);
        Assert.Equal("pending", (string?)_api.Subscription(replacement)!["status"]);
        Assert.Equal(periodEnd, DateTimeOffset.Parse((string)_api.Subscription(replacement)!["next_payment_date"]!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("cancelled", (string?)_api.Subscription(old)!["status"]);

        // Paystack reports the old one disabled: part of the change, not the end of the service.
        await DeliverAndProcessAsync(_api.Disabled(old));
        Assert.Equal("Active", await ScalarAsync("SELECT state::text FROM cp.subscription WHERE tenant_id = @t", t.TenantId));

        // The replacement's first period is paid, then the change applies at the boundary and the subscription moves to it.
        await DeliverAndProcessAsync(_api.Renew(replacement, success: true));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM cp.webhook_inbox WHERE tenant_id = @t AND processing_state = 'Quarantined'", t.TenantId));
        await using var app = DependencyInjection.BuildDataSource(fx.AppConnection);
        await using var platform = DependencyInjection.BuildDataSource(fx.PlatformConnection);
        var clock = new FakeTimeProvider(periodEnd.AddMinutes(1));
        var job = new PlanChangeJob(fx.Factory.Services.GetRequiredService<ControlDataSource>(),
            new TenantJobRunner(new PlatformDataSource(platform), new AppDataSource(app), clock), Factory.Services.GetRequiredService<IBillingProvider>(), clock,
            NullLogger<PlanChangeJob>.Instance);
        Assert.Equal(SubscriptionChangeState.Applied, await job.ApplyAsync(Guid.Parse((string)change["id"]!), CancellationToken.None));
        Assert.Equal(replacement, (string?)await ScalarAsync("SELECT provider_subscription_ref FROM cp.subscription WHERE tenant_id = @t", t.TenantId));
        Assert.Equal(100L, (long)(await SubscriptionAsync(t))["limits"]!["active_warehouses"]!);
    }

    [Theory]
    [Trait("Requirement", "AGT-004")]
    [Trait("Requirement", "BIL-002")]
    [InlineData("sk_live_0123456789abcdef0123456789")]
    [InlineData("pk_test_0123456789abcdef0123456789")]
    [InlineData("")]
    public void Paystack_starts_only_with_a_test_secret_key(string key)
    {
        IConfiguration Config(string k) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:Provider"] = "paystack", ["Billing:Paystack:SecretKey"] = k, ["Billing:InboxKey"] = Convert.ToBase64String(ApiFixture.InboxKey),
        }).Build();
        new ServiceCollection().AddBilling(Config(_api.SecretKey));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddBilling(Config(key)));
    }

    // ---- Helpers ----

    private sealed record Arranged(HttpClient Owner, Guid TenantId);

    private WebApplicationFactory<Program> Build(params (string Key, string Value)[] settings) =>
        fx.Factory.WithWebHostBuilder(web =>
        {
            web.UseSetting("Billing:Provider", "paystack");
            web.UseSetting("Billing:Paystack:SecretKey", _api.SecretKey);
            foreach (var (key, value) in settings)
                web.UseSetting(key, value);
            web.ConfigureServices(s => s.AddSingleton(new PaystackTransport(_api)));
        });

    private HttpClient Client(string subject, Guid? tenantId)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue(subject));
        if (tenantId is { } t)
            client.DefaultRequestHeaders.Add("X-Tenant-Id", t.ToString());
        return client;
    }

    private async Task<Arranged> TenantAsync(string label)
    {
        var tenant = await TenantProvisioning.SignUpAsync(fx, label);
        return new Arranged(Client(tenant.Admin, tenant.TenantId), tenant.TenantId);
    }

    private async Task<Arranged> ActiveAsync(string label, string[] features, long limitEach)
    {
        var t = await TenantAsync(label);
        var price = await NgnPriceAsync(500000, features, limitEach);
        var digest = (string)(await (await t.Owner.PostJsonAsync("/api/v1/saas/checkout/preview", new { priceVersionId = price })).ExpectAsync(HttpStatusCode.OK))["previewDigest"]!;
        var session = await CheckoutAsync(t, price, digest);
        await DeliverAndProcessAsync(_api.Pay("ti-cs-" + Guid.Parse((string)session["checkoutId"]!).ToString("N")));
        Assert.Equal("Active", (string?)(await SubscriptionAsync(t))["state"]);
        return t;
    }

    /// <summary>A published public plan with one monthly NGN price, set by platform billing in the catalog (D09).</summary>
    private async Task<Guid> NgnPriceAsync(long kobo, string[] features, long limitEach)
    {
        var billing = Client(SeedData.PlatformBilling, null);
        var plan = await (await billing.PostJsonAsync("/platform/v1/plan-versions", new
        {
            planCode = "ps-" + Guid.NewGuid().ToString("N")[..12], name = "Paystack test plan", features,
            limits = new { active_seats = limitEach, active_warehouses = limitEach, active_skus = limitEach, active_devices = limitEach, attachment_bytes = limitEach * 1000 },
            trialDays = 14, reason = "paystack test",
        })).ExpectAsync(HttpStatusCode.Created);
        var price = await (await billing.PostJsonAsync($"/platform/v1/plan-versions/{plan["id"]}/price-versions", new
        {
            amount = new { currency = "NGN", minorUnits = kobo, exponent = 2 }, interval = "Month", activeFrom = DateTimeOffset.UtcNow.AddMinutes(-1), reason = "paystack test",
        }, "\"1\"")).ExpectAsync(HttpStatusCode.Created);
        await (await billing.PostJsonAsync($"/platform/v1/plan-versions/{plan["id"]}/publish", new { reason = "paystack test" }, "\"2\"")).ExpectAsync(HttpStatusCode.OK);
        return Guid.Parse((string)price["id"]!);
    }

    private static async Task<JsonObject> CheckoutAsync(Arranged t, Guid price, string digest)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saas/checkout")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { priceVersionId = price, acceptedPreviewDigest = digest }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await (await t.Owner.SendAsync(request)).ExpectAsync(HttpStatusCode.Created);
    }

    private static async Task<JsonObject> SubscriptionAsync(Arranged t) =>
        await (await t.Owner.GetAsync("/api/v1/saas/subscription")).ExpectAsync(HttpStatusCode.OK);

    private static HttpRequestMessage Request(PaystackEvent e, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(e.Body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (e.Signature.Length > 0)
            request.Headers.TryAddWithoutValidation(PaystackBillingProvider.SignatureHeader, e.Signature);
        return request;
    }

    private Task<HttpResponseMessage> DeliverAsync(PaystackEvent e, string url = Url) => Factory.CreateClient().SendAsync(Request(e, url));

    private Task ProcessAsync() => Factory.Services.GetRequiredService<BillingEventJob>().RunOnceAsync(CancellationToken.None);

    private async Task DeliverAndProcessAsync(PaystackEvent e)
    {
        await (await DeliverAsync(e)).ExpectAsync(HttpStatusCode.OK);
        await ProcessAsync();
    }

    private async Task<object?> ScalarAsync(string sql, Guid? tenantId, params (string Name, object Value)[] args)
    {
        var all = tenantId is { } t ? args.Append(("t", (object)t)).ToArray() : args;
        var result = await TenantProvisioning.ScalarAsSuperuserAsync(fx, sql, all);
        return result is DBNull ? null : result;
    }

    private Task<object?> ScalarAsync(string sql, Guid tenantId) => ScalarAsync(sql, tenantId, []);

    private async Task<long> CountAsync(string sql, Guid tenantId) =>
        Convert.ToInt64(await ScalarAsync(sql, tenantId), System.Globalization.CultureInfo.InvariantCulture);
}
