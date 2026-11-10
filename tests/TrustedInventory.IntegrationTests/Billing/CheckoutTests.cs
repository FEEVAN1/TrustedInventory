using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TrustedInventory.Infrastructure.Billing;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Billing;

/// <summary>Preview, consent, hosted checkout and the billing portal against the labelled simulator (BIL-002, BIL-003, DEC-063).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class CheckoutTests(ApiFixture fx)
{
    private static readonly string[] CoreOnly = ["core_inventory"];

    private SimulatedBillingProvider Simulator => fx.Factory.Services.GetRequiredService<SimulatedBillingProvider>();

    [Fact]
    [Trait("Requirement", "BIL-003")]
    [Trait("Requirement", "BIL-002")]
    [Trait("Requirement", "AT-BIL-01")]
    public async Task An_accepted_preview_opens_one_hosted_checkout_and_the_trial_is_unchanged_until_payment_is_verified()
    {
        var (owner, tenantId) = await OwnerAsync("co-happy");
        var price = await PublishedPriceAsync(2500);

        var preview = await (await owner.PostJsonAsync("/api/v1/saas/checkout/preview", new { priceVersionId = price })).ExpectAsync(HttpStatusCode.OK);
        var digest = (string)preview["previewDigest"]!;
        Assert.Matches("^[0-9a-f]{64}$", digest);
        Assert.Equal(2500L, (long)preview["amount"]!["minorUnits"]!);
        Assert.Equal("Month", (string?)preview["interval"]);
        Assert.Contains("XTS 25.00", (string?)preview["renewal"]);
        Assert.Contains("end of the paid period", (string?)preview["cancellationPolicy"]);
        // The same terms give the same digest; the time is not part of it.
        var again = await (await owner.PostJsonAsync("/api/v1/saas/checkout/preview", new { priceVersionId = price })).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(digest, (string?)again["previewDigest"]);

        var key = Guid.NewGuid();
        var created = await Checkout(owner, key, price, digest);
        var session = await created.ExpectAsync(HttpStatusCode.Created);
        var id = (string)session["checkoutId"]!;
        Assert.Equal($"/api/v1/saas/checkout/{id}", created.Headers.Location?.ToString());
        Assert.StartsWith("https://billing-simulator.invalid/checkout/", (string?)session["hostedUrl"]);
        Assert.Equal("Open", (string?)session["status"]);
        Assert.Null(session["retryGuidance"]);

        // Replaying the key returns the same session and creates nothing new at the provider.
        var objects = Simulator.CreatedObjects;
        var replay = await (await Checkout(owner, key, price, digest)).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(id, (string?)replay["checkoutId"]);
        Assert.Equal((string?)session["hostedUrl"], (string?)replay["hostedUrl"]);
        Assert.Equal(objects, Simulator.CreatedObjects);

        // Coming back from the hosted page proves nothing: the session stays open and the trial is untouched (AT-BIL-01).
        var polled = await (await owner.GetAsync($"/api/v1/saas/checkout/{id}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Open", (string?)polled["status"]);
        var subscription = await (await owner.GetAsync("/api/v1/saas/subscription")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Trialing", (string?)subscription["state"]);
        Assert.Null(subscription["priceVersionId"]);

        await using var conn = await OpenAsync();
        Assert.Equal(digest, await ScalarAsync<string>(conn, "SELECT encode(accepted_preview_digest, 'hex') FROM cp.checkout_session WHERE id = @id",
            ("id", Guid.Parse(id))));
        Assert.StartsWith("sim_cus_", await ScalarAsync<string>(conn, "SELECT provider_customer_id FROM cp.billing_account WHERE tenant_id = @t",
            ("t", tenantId)));
        Assert.StartsWith("sim_price_", await ScalarAsync<string>(conn, "SELECT provider_price_ref FROM cp.price_version WHERE id = @p", ("p", price)));
        Assert.Equal(1L, await ScalarAsync<long>(conn,
            "SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'CheckoutStarted' AND object_id = @id",
            ("t", tenantId), ("id", Guid.Parse(id))));
    }

    [Fact]
    [Trait("Requirement", "BIL-003")]
    public async Task Checkout_needs_the_digest_of_the_current_terms_and_a_key_is_bound_to_its_first_request()
    {
        var (owner, tenantId) = await OwnerAsync("co-consent");
        var price = await PublishedPriceAsync(2500);
        var other = await PublishedPriceAsync(9900);

        await (await Checkout(owner, Guid.NewGuid(), price, new string('0', 64))).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        // A digest accepted for another price does not carry over.
        var otherDigest = await DigestAsync(owner, other);
        await (await Checkout(owner, Guid.NewGuid(), price, otherDigest)).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        await using var conn = await OpenAsync();
        Assert.Equal(0L, await ScalarAsync<long>(conn, "SELECT count(*) FROM cp.checkout_session WHERE tenant_id = @t", ("t", tenantId)));

        var key = Guid.NewGuid();
        await (await Checkout(owner, key, price, await DigestAsync(owner, price))).ExpectAsync(HttpStatusCode.Created);
        await (await Checkout(owner, key, other, otherDigest)).ExpectProblemAsync(HttpStatusCode.Conflict, "IDEMPOTENCY_MISMATCH");
        var missingKey = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saas/checkout")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { priceVersionId = price, acceptedPreviewDigest = await DigestAsync(owner, price) }),
        };
        await (await owner.SendAsync(missingKey)).ExpectProblemAsync(HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_REQUIRED");
    }

    [Fact]
    [Trait("Requirement", "BIL-003")]
    [Trait("Requirement", "BIL-002")]
    public async Task A_new_checkout_replaces_the_open_one_and_an_expired_session_says_how_to_retry()
    {
        var (owner, tenantId) = await OwnerAsync("co-replace");
        var price = await PublishedPriceAsync(2500);
        var digest = await DigestAsync(owner, price);
        var first = (string)(await (await Checkout(owner, Guid.NewGuid(), price, digest)).ExpectAsync(HttpStatusCode.Created))["checkoutId"]!;
        var second = (string)(await (await Checkout(owner, Guid.NewGuid(), price, digest)).ExpectAsync(HttpStatusCode.Created))["checkoutId"]!;
        Assert.NotEqual(first, second);

        var replaced = await (await owner.GetAsync($"/api/v1/saas/checkout/{first}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Expired", (string?)replaced["status"]);
        Assert.Contains("start a new checkout", (string?)replaced["retryGuidance"]);

        // Past its expiry an open session reads as Expired without anything writing to it.
        await using var conn = await OpenAsync();
        await ExecAsync(conn, "UPDATE cp.checkout_session SET expires_at = now() - interval '1 minute', created_at = now() - interval '2 hours' WHERE id = @id",
            ("id", Guid.Parse(second)));
        var lapsed = await (await owner.GetAsync($"/api/v1/saas/checkout/{second}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Expired", (string?)lapsed["status"]);
        Assert.NotNull(lapsed["retryGuidance"]);
        Assert.Equal("Open", await ScalarAsync<string>(conn, "SELECT status::text FROM cp.checkout_session WHERE id = @id", ("id", Guid.Parse(second))));
        Assert.Equal(1L, await ScalarAsync<long>(conn,
            "SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'CheckoutExpired' AND object_id = @id",
            ("t", tenantId), ("id", Guid.Parse(first))));

        // Another tenant's session is not found.
        var (stranger, _) = await OwnerAsync("co-stranger");
        await (await stranger.GetAsync($"/api/v1/saas/checkout/{first}")).ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    [Trait("Requirement", "BIL-002")]
    public async Task A_provider_outage_is_503_and_the_same_key_finishes_the_checkout_afterwards()
    {
        var (owner, tenantId) = await OwnerAsync("co-outage");
        var price = await PublishedPriceAsync(2500);
        var digest = await DigestAsync(owner, price);
        var key = Guid.NewGuid();
        Simulator.Outage = true;
        try
        {
            await (await Checkout(owner, key, price, digest)).ExpectProblemAsync(HttpStatusCode.ServiceUnavailable, "SERVICE_UNAVAILABLE");
            await (await owner.PostEmptyAsync("/api/v1/saas/billing-portal")).ExpectProblemAsync(HttpStatusCode.ServiceUnavailable, "SERVICE_UNAVAILABLE");
        }
        finally
        {
            Simulator.Outage = false;
        }
        await using var conn = await OpenAsync();
        var pending = await ScalarAsync<Guid>(conn, "SELECT id FROM cp.checkout_session WHERE tenant_id = @t AND hosted_url IS NULL", ("t", tenantId));
        // The caller never received this id, and it cannot be polled until it has a provider page.
        await (await owner.GetAsync($"/api/v1/saas/checkout/{pending}")).ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");

        var done = await (await Checkout(owner, key, price, digest)).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(pending.ToString(), (string?)done["checkoutId"]);
        Assert.StartsWith("https://billing-simulator.invalid/checkout/", (string?)done["hostedUrl"]);
    }

    [Fact]
    [Trait("Requirement", "BIL-003")]
    public async Task Only_owners_and_billing_administrators_check_out_and_only_for_an_unpaid_subscription_and_a_live_price()
    {
        var price = await PublishedPriceAsync(2500);
        var admin = fx.Client(fx.Seed.A.Admin, fx.Seed.A.TenantId);
        await (await admin.PostJsonAsync("/api/v1/saas/checkout/preview", new { priceVersionId = price })).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await admin.PostEmptyAsync("/api/v1/saas/billing-portal")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        var (owner, tenantId) = await OwnerAsync("co-eligible");
        // Unknown, draft and not-yet-active prices are not offered.
        await (await owner.PostJsonAsync("/api/v1/saas/checkout/preview", new { priceVersionId = Guid.NewGuid() })).ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await owner.PostJsonAsync("/api/v1/saas/checkout/preview", new { priceVersionId = await DraftPriceAsync() }))
            .ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await owner.PostJsonAsync("/api/v1/saas/checkout/preview", new { priceVersionId = await PublishedPriceAsync(2500, DateTimeOffset.UtcNow.AddDays(3)) }))
            .ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");

        // A paid subscription changes plan instead (slice 4).
        await using var conn = await OpenAsync();
        await ExecAsync(conn, """
            UPDATE cp.subscription SET state = 'Active', price_version_id = @p, period_start = now(), period_end = now() + interval '30 days',
                   revision = revision + 1 WHERE tenant_id = @t
            """, ("p", price), ("t", tenantId));
        await (await owner.PostJsonAsync("/api/v1/saas/checkout/preview", new { priceVersionId = price })).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
    }

    [Fact]
    [Trait("Requirement", "BIL-002")]
    public async Task The_billing_portal_is_a_short_lived_provider_link_and_is_audited()
    {
        var (owner, tenantId) = await OwnerAsync("co-portal");
        var before = DateTimeOffset.UtcNow;
        var portal = await (await owner.PostEmptyAsync("/api/v1/saas/billing-portal")).ExpectAsync(HttpStatusCode.Created);
        Assert.StartsWith("https://billing-simulator.invalid/portal/", (string?)portal["url"]);
        var expires = DateTimeOffset.Parse((string)portal["expiresAt"]!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(expires, before, DateTimeOffset.UtcNow.AddMinutes(5).AddSeconds(5));
        Assert.Equal(["expiresAt", "url"], portal.Select(p => p.Key).Order());

        await using var conn = await OpenAsync();
        Assert.Equal(1L, await ScalarAsync<long>(conn, "SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'BillingPortalOpened'",
            ("t", tenantId)));
    }

    private async Task<(HttpClient Owner, Guid TenantId)> OwnerAsync(string label)
    {
        var tenant = await TenantProvisioning.SignUpAsync(fx, label);
        return (fx.Client(tenant.Admin, tenant.TenantId), tenant.TenantId);
    }

    private static Task<HttpResponseMessage> Checkout(HttpClient owner, Guid key, Guid price, string digest)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saas/checkout")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { priceVersionId = price, acceptedPreviewDigest = digest }),
        };
        request.Headers.Add("Idempotency-Key", key.ToString());
        return owner.SendAsync(request);
    }

    private static async Task<string> DigestAsync(HttpClient owner, Guid price) =>
        (string)(await (await owner.PostJsonAsync("/api/v1/saas/checkout/preview", new { priceVersionId = price })).ExpectAsync(HttpStatusCode.OK))["previewDigest"]!;

    /// <summary>A published public plan with one monthly XTS price, through the platform catalog API.</summary>
    internal static async Task<Guid> PublishedPriceAsync(ApiFixture fx, long amount, DateTimeOffset? activeFrom = null, bool publish = true,
        string[]? features = null, long limitEach = 1000)
    {
        var billing = fx.Client(SeedData.PlatformBilling, tenantId: null);
        var plan = await (await billing.PostJsonAsync("/platform/v1/plan-versions", new
        {
            planCode = "co-" + Guid.NewGuid().ToString("N")[..12], name = "Checkout test plan",
            features = features ?? CoreOnly,
            limits = new { active_seats = limitEach, active_warehouses = limitEach, active_skus = limitEach, active_devices = limitEach, attachment_bytes = limitEach * 1000 },
            trialDays = 14, reason = "checkout test",
        })).ExpectAsync(HttpStatusCode.Created);
        var price = await (await billing.PostJsonAsync($"/platform/v1/plan-versions/{plan["id"]}/price-versions", new
        {
            amount = new { currency = "XTS", minorUnits = amount, exponent = 2 }, interval = "Month",
            activeFrom = activeFrom ?? DateTimeOffset.UtcNow.AddMinutes(-1), reason = "checkout test",
        }, "\"1\"")).ExpectAsync(HttpStatusCode.Created);
        if (publish)
            await (await billing.PostJsonAsync($"/platform/v1/plan-versions/{plan["id"]}/publish", new { reason = "checkout test" }, "\"2\""))
                .ExpectAsync(HttpStatusCode.OK);
        return Guid.Parse((string)price["id"]!);
    }

    private Task<Guid> PublishedPriceAsync(long amount, DateTimeOffset? activeFrom = null) => PublishedPriceAsync(fx, amount, activeFrom);

    private Task<Guid> DraftPriceAsync() => PublishedPriceAsync(fx, 2500, publish: false);

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
