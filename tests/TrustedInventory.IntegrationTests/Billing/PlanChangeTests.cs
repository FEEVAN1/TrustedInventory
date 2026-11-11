using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TrustedInventory.Application.Billing;
using TrustedInventory.Application.Saas;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Infrastructure;
using TrustedInventory.Infrastructure.Billing;
using TrustedInventory.Infrastructure.Jobs;
using TrustedInventory.Infrastructure.Persistence;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Billing;

/// <summary>Plan changes at the next billing cycle, downgrade blockers, cancel and resume, against the simulator (DEC-065).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class PlanChangeTests(ApiFixture fx)
{
    private static readonly string[] CoreOnly = ["core_inventory"];
    private static readonly string[] CoreAndMedical = ["core_inventory", "medical_storage"];
    private static readonly string[] Growth = ["core_inventory", "service_api", "advanced_reporting"];

    private SimulatedBillingProvider Simulator => fx.Factory.Services.GetRequiredService<SimulatedBillingProvider>();

    [Fact]
    [Trait("Requirement", "AT-ENT-02")]
    [Trait("Requirement", "ENT-006")]
    public async Task A_downgrade_with_medical_stock_is_refused_with_exact_blockers_and_nothing_is_scheduled()
    {
        var t = await ActiveAsync("pc-medical", CoreAndMedical, 1000);
        await MedicalStockOnHandAsync(t);
        var coreOnly = await CheckoutTests.PublishedPriceAsync(fx, 1500, features: CoreOnly, limitEach: 10);

        var preview = await PreviewAsync(t, coreOnly);
        var blocker = Assert.Single(preview["blockers"]!.AsArray())!;
        Assert.Equal("MEDICAL_STOCK_ON_HAND", (string?)blocker["code"]);
        Assert.Equal(1L, (long)blocker["used"]!);

        var refused = await ChangeAsync(t, Guid.NewGuid(), coreOnly, (string)preview["previewDigest"]!, await RevisionAsync(t));
        var problem = await refused.ExpectProblemAsync(HttpStatusCode.Conflict, "DOWNGRADE_BLOCKED");
        Assert.Equal("MEDICAL_STOCK_ON_HAND", (string?)problem["blockers"]![0]!["code"]);
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM cp.subscription_change WHERE tenant_id = @t", t.TenantId));

        // Usage above a target limit is a blocker with the quota, the usage and the limit; nothing is trimmed to fit.
        var tiny = await CheckoutTests.PublishedPriceAsync(fx, 500, features: CoreAndMedical, limitEach: 0);
        var quota = (await PreviewAsync(t, tiny))["blockers"]!.AsArray()
            .Single(b => (string?)b!["code"] == "QUOTA_EXCEEDED" && (string?)b["quota"] == "active_seats")!;
        Assert.Equal(1L, (long)quota["used"]!);
        Assert.Equal(0L, (long)quota["limit"]!);
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.membership WHERE tenant_id = @t AND active", t.TenantId));
    }

    [Fact]
    [Trait("Requirement", "ENT-006")]
    [Trait("Requirement", "AT-ENT-02")]
    public async Task Medical_stock_received_after_scheduling_blocks_the_downgrade_at_the_boundary()
    {
        var t = await ActiveAsync("pc-medical-late", CoreAndMedical, 1000);
        var coreOnly = await CheckoutTests.PublishedPriceAsync(fx, 1500, features: CoreOnly, limitEach: 10);
        var change = await ScheduleAsync(t, coreOnly);
        await MedicalStockOnHandAsync(t);

        Assert.Equal(SubscriptionChangeState.Blocked, await ApplyAsync(change, (await PeriodEndAsync(t)).AddMinutes(1)));
        Assert.Contains("MEDICAL_STOCK_ON_HAND", (string?)await ScalarAsync("SELECT blockers::text FROM cp.subscription_change WHERE id = @id", null, ("id", change)));
        var sub = await (await t.Owner.GetAsync("/api/v1/saas/subscription")).ExpectAsync(HttpStatusCode.OK);
        Assert.NotEqual(coreOnly.ToString(), (string?)sub["priceVersionId"]);
        Assert.Equal(5m, Convert.ToDecimal(await ScalarAsync("SELECT sum(on_hand_qty) FROM ti.stock_bucket WHERE tenant_id = @t", t.TenantId),
            System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait("Requirement", "ENT-006")]
    [Trait("Requirement", "AT-ENT-03")]
    public async Task A_quota_increase_racing_the_boundary_is_seen_by_the_apply_time_recheck()
    {
        var t = await ActiveAsync("pc-race", CoreOnly, 10);
        var smaller = await CheckoutTests.PublishedPriceAsync(fx, 900, features: CoreOnly, limitEach: 2);
        var change = await ScheduleAsync(t, smaller);

        // A concurrent command holds the warehouse counter (as QuotaGate does) with an uncommitted increase past the target limit.
        await using var racer = new Npgsql.NpgsqlConnection(fx.SuperuserConnection);
        await racer.OpenAsync();
        await using var tx = await racer.BeginTransactionAsync();
        await using (var hold = new Npgsql.NpgsqlCommand(
            "UPDATE ti.usage_counter SET used = used + 3 WHERE tenant_id = @t AND metric = 'active_warehouses'", racer, tx))
        {
            hold.Parameters.AddWithValue("t", t.TenantId);
            Assert.Equal(1, await hold.ExecuteNonQueryAsync());
        }

        // The tenant pass sees the committed usage (it fits) and the apply then waits on the counter lock.
        var apply = ApplyAsync(change, (await PeriodEndAsync(t)).AddMinutes(1));
        var waited = false;
        for (var i = 0; i < 100 && !waited && !apply.IsCompleted; i++)
        {
            await Task.Delay(50);
            waited = Convert.ToInt64(await TenantProvisioning.ScalarAsSuperuserAsync(fx,
                "SELECT count(*) FROM pg_stat_activity WHERE usename = 'ti_control' AND wait_event_type = 'Lock'"),
                System.Globalization.CultureInfo.InvariantCulture) > 0;
        }
        Assert.True(waited, "the apply did not wait for the counter lock");
        await tx.CommitAsync();

        Assert.Equal(SubscriptionChangeState.Blocked, await apply);
        Assert.Equal(10L, (long)(await (await t.Owner.GetAsync("/api/v1/saas/subscription")).ExpectAsync(HttpStatusCode.OK))["limits"]!["active_warehouses"]!);
    }

    [Fact]
    [Trait("Requirement", "ENT-005")]
    public async Task An_upgrade_is_scheduled_for_the_period_end_and_applies_only_after_its_first_period_is_paid()
    {
        var t = await ActiveAsync("pc-upgrade", CoreOnly, 10);
        var growth = await CheckoutTests.PublishedPriceAsync(fx, 9900, features: Growth, limitEach: 100);
        var periodEnd = await PeriodEndAsync(t);

        var preview = await PreviewAsync(t, growth);
        Assert.Equal(periodEnd, DateTimeOffset.Parse((string)preview["effectiveAt"]!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(9900L, (long)preview["amount"]!["minorUnits"]!);
        Assert.Empty(preview["blockers"]!.AsArray());
        var digest = (string)preview["previewDigest"]!;

        var key = Guid.NewGuid();
        var revision = await RevisionAsync(t);
        var response = await ChangeAsync(t, key, growth, digest, revision);
        var change = await response.ExpectAsync(HttpStatusCode.Accepted);
        Assert.Equal("Scheduled", (string?)change["state"]);
        Assert.Equal($"\"{revision + 1}\"", response.Headers.ETag?.Tag);
        // A retry after a lost response returns the same change, even with the old If-Match; another body under the key is refused.
        Assert.Equal((string?)change["id"], (string?)(await (await ChangeAsync(t, key, growth, digest, revision)).ExpectAsync(HttpStatusCode.Accepted))["id"]);
        var other = await CheckoutTests.PublishedPriceAsync(fx, 4900, features: Growth, limitEach: 50);
        await (await ChangeAsync(t, key, other, digest, revision + 1)).ExpectProblemAsync(HttpStatusCode.Conflict, "IDEMPOTENCY_MISMATCH");
        var sub = await (await t.Owner.GetAsync("/api/v1/saas/subscription")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal((string?)change["id"], (string?)sub["scheduledChange"]!["id"]);

        // At the boundary the current plan stays in force until the upgraded period is paid (ENT-005).
        Assert.Equal(SubscriptionChangeState.Scheduled, await ApplyAsync(Guid.Parse((string)change["id"]!), periodEnd.AddMinutes(1)));
        Assert.Equal(10L, (long)(await (await t.Owner.GetAsync("/api/v1/saas/subscription")).ExpectAsync(HttpStatusCode.OK))["limits"]!["active_warehouses"]!);

        await DeliverAndProcessAsync(Simulator.PayRenewal(t.SubscriptionRef));
        Assert.Equal(9900L, await CountAsync("SELECT total FROM cp.invoice WHERE tenant_id = @t ORDER BY period_start DESC LIMIT 1", t.TenantId));
        Assert.Equal(SubscriptionChangeState.Applied, await ApplyAsync(Guid.Parse((string)change["id"]!), periodEnd.AddMinutes(1)));

        sub = await (await t.Owner.GetAsync("/api/v1/saas/subscription")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(growth.ToString(), (string?)sub["priceVersionId"]);
        Assert.Equal(100L, (long)sub["limits"]!["active_warehouses"]!);
        Assert.Null(sub["scheduledChange"]);
        Assert.Equal(3L, await CountAsync("SELECT max(entitlement_version) FROM cp.entitlement_snapshot WHERE tenant_id = @t", t.TenantId));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM cp.billing_notice WHERE tenant_id = @t AND kind = 'PlanChangeApplied'", t.TenantId));
    }

    [Fact]
    [Trait("Requirement", "ENT-006")]
    public async Task A_downgrade_applies_at_the_boundary_when_it_fits_and_is_blocked_when_usage_rose_first()
    {
        // Fits: applies at the boundary without waiting for a payment.
        var fits = await ActiveAsync("pc-fits", CoreOnly, 10);
        var smaller = await CheckoutTests.PublishedPriceAsync(fx, 900, features: CoreOnly, limitEach: 2);
        var fitsChange = await ScheduleAsync(fits, smaller);
        Assert.Equal(SubscriptionChangeState.Applied, await ApplyAsync(fitsChange, (await PeriodEndAsync(fits)).AddMinutes(1)));
        Assert.Equal(2L, (long)(await (await fits.Owner.GetAsync("/api/v1/saas/subscription")).ExpectAsync(HttpStatusCode.OK))["limits"]!["active_warehouses"]!);

        // Usage rises above the target before the boundary: blocked, the owner is told, nothing is removed.
        var rose = await ActiveAsync("pc-rose", CoreOnly, 10);
        var roseChange = await ScheduleAsync(rose, smaller);
        Assert.Equal(smaller.ToString("N"), Simulator.Subscription(rose.SubscriptionRef)!.PendingPriceRef![^32..]);
        for (var i = 0; i < 3; i++)
            await (await rose.Owner.PostJsonAsync("/api/v1/warehouses", new { code = $"W{i}", name = $"Warehouse {i}", timeZone = "UTC" }))
                .ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(SubscriptionChangeState.Blocked, await ApplyAsync(roseChange, (await PeriodEndAsync(rose)).AddMinutes(1)));

        var blockers = JsonNode.Parse((string)(await ScalarAsync("SELECT blockers::text FROM cp.subscription_change WHERE id = @id", null, ("id", roseChange)))!)!.AsArray();
        var warehouses = blockers.Single(b => (string?)b!["quota"] == "active_warehouses")!;
        Assert.Equal(3L, (long)warehouses["used"]!);
        Assert.Equal(2L, (long)warehouses["limit"]!);
        Assert.Equal(10L, (long)(await (await rose.Owner.GetAsync("/api/v1/saas/subscription")).ExpectAsync(HttpStatusCode.OK))["limits"]!["active_warehouses"]!);
        Assert.Equal(3L, await CountAsync("SELECT count(*) FROM ti.warehouse WHERE tenant_id = @t AND active", rose.TenantId));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM cp.billing_notice WHERE tenant_id = @t AND kind = 'PlanChangeBlocked'", rose.TenantId));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'SubscriptionChangeBlocked'", rose.TenantId));
        Assert.Null(Simulator.Subscription(rose.SubscriptionRef)!.PendingPriceRef);
    }

    [Fact]
    [Trait("Requirement", "ENT-005")]
    public async Task Changes_need_a_paid_subscription_the_current_revision_and_the_accepted_terms()
    {
        var trial = await TenantProvisioning.SignUpAsync(fx, "pc-trial");
        var trialOwner = fx.Client(trial.Admin, trial.TenantId);
        var price = await CheckoutTests.PublishedPriceAsync(fx, 1900, features: CoreOnly, limitEach: 10);
        await (await trialOwner.PostJsonAsync("/api/v1/saas/subscription/change/preview", new { targetPriceVersionId = price }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");

        var t = await ActiveAsync("pc-rules", CoreOnly, 10);
        await (await t.Owner.PostJsonAsync("/api/v1/saas/subscription/change/preview", new { targetPriceVersionId = t.PriceId }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        await (await t.Owner.PostJsonAsync("/api/v1/saas/subscription/change/preview", new { targetPriceVersionId = Guid.NewGuid() }))
            .ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        var digest = (string)(await PreviewAsync(t, price))["previewDigest"]!;
        var revision = await RevisionAsync(t);
        await (await ChangeAsync(t, Guid.NewGuid(), price, digest, revision + 7)).ExpectProblemAsync(HttpStatusCode.PreconditionFailed, "VERSION_CONFLICT");
        await (await ChangeAsync(t, Guid.NewGuid(), price, digest, null)).ExpectProblemAsync(HttpStatusCode.PreconditionRequired, "PRECONDITION_REQUIRED");
        await (await ChangeAsync(t, Guid.NewGuid(), price, new string('a', 64), revision)).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        // Members without billing.manage cannot change plans.
        await (await fx.Client(fx.Seed.A.Admin, fx.Seed.A.TenantId).PostJsonAsync("/api/v1/saas/subscription/change/preview",
            new { targetPriceVersionId = price })).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM cp.subscription_change WHERE tenant_id = @t", t.TenantId));
    }

    [Fact]
    [Trait("Requirement", "ENT-005")]
    public async Task A_provider_outage_leaves_a_pending_change_that_the_same_key_completes()
    {
        var t = await ActiveAsync("pc-outage", CoreOnly, 10);
        var price = await CheckoutTests.PublishedPriceAsync(fx, 1900, features: CoreOnly, limitEach: 20);
        var digest = (string)(await PreviewAsync(t, price))["previewDigest"]!;
        var key = Guid.NewGuid();
        var revision = await RevisionAsync(t);
        Simulator.Outage = true;
        try
        {
            await (await ChangeAsync(t, key, price, digest, revision)).ExpectProblemAsync(HttpStatusCode.ServiceUnavailable, "SERVICE_UNAVAILABLE");
        }
        finally
        {
            Simulator.Outage = false;
        }
        Assert.Equal("Pending", await ScalarAsync("SELECT state::text FROM cp.subscription_change WHERE tenant_id = @t", t.TenantId));
        var done = await (await ChangeAsync(t, key, price, digest, revision)).ExpectAsync(HttpStatusCode.Accepted);
        Assert.Equal("Scheduled", (string?)done["state"]);
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM cp.subscription_change WHERE tenant_id = @t", t.TenantId));
    }

    [Fact]
    [Trait("Requirement", "BIL-008")]
    [Trait("Requirement", "LFC-002")]
    public async Task Cancellation_takes_effect_at_period_end_keeps_data_and_resume_withdraws_only_a_pending_one()
    {
        var t = await ActiveAsync("pc-cancel", CoreOnly, 10);
        var smaller = await CheckoutTests.PublishedPriceAsync(fx, 900, features: CoreOnly, limitEach: 5);
        var change = await ScheduleAsync(t, smaller);
        var periodEnd = await PeriodEndAsync(t);

        await (await fx.Client(fx.Seed.A.Admin, fx.Seed.A.TenantId).PostJsonAsync("/api/v1/saas/subscription/cancel", new { reason = "x" }, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        var cancelled = await (await t.Owner.PostJsonAsync("/api/v1/saas/subscription/cancel", new { reason = "Moving to another tool" },
            $"\"{await RevisionAsync(t)}\"")).ExpectAsync(HttpStatusCode.OK);
        Assert.True((bool)cancelled["cancelAtPeriodEnd"]!);
        Assert.Equal(periodEnd, DateTimeOffset.Parse((string)cancelled["cancellationEffectiveAt"]!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("Cancelled", await ScalarAsync("SELECT state::text FROM cp.subscription_change WHERE id = @id", null, ("id", change)));
        Assert.True(Simulator.Subscription(t.SubscriptionRef)!.CancelAtPeriodEnd);
        // Repeating it changes nothing.
        var again = await (await t.Owner.PostJsonAsync("/api/v1/saas/subscription/cancel", new { reason = "again" }, $"\"{await RevisionAsync(t)}\""))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal((int)cancelled["version"]!, (int)again["version"]!);

        // Service continues until period end, then is restricted by computation alone; nothing is deleted.
        var access = await fx.Factory.Services.GetRequiredService<ITenantAccessReader>().ReadAsync(t.TenantId, CancellationToken.None);
        Assert.Equal(AccessMode.Normal, access!.Mode(periodEnd.AddMinutes(-1)));
        Assert.Equal(AccessMode.BillingRestricted, access.Mode(periodEnd));
        Assert.Equal(RestrictionReason.SubscriptionEnded, access.BillingReason(periodEnd));

        var resumed = await (await t.Owner.PostEmptyAsync("/api/v1/saas/subscription/resume", $"\"{await RevisionAsync(t)}\"")).ExpectAsync(HttpStatusCode.OK);
        Assert.False((bool)resumed["cancelAtPeriodEnd"]!);
        Assert.False(Simulator.Subscription(t.SubscriptionRef)!.CancelAtPeriodEnd);
        await (await t.Owner.PostEmptyAsync("/api/v1/saas/subscription/resume", $"\"{await RevisionAsync(t)}\""))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");

        // Cancelled again and ended by the provider: resume is refused and a new checkout is needed.
        await (await t.Owner.PostJsonAsync("/api/v1/saas/subscription/cancel", new { reason = "Closing for now" }, $"\"{await RevisionAsync(t)}\""))
            .ExpectAsync(HttpStatusCode.OK);
        await DeliverAndProcessAsync(Simulator.EndSubscription(t.SubscriptionRef));
        // The provider's end does not cut the paid period short; the period-end job records it afterwards (DEC-068).
        Assert.Equal("Active", await ScalarAsync("SELECT state::text FROM cp.subscription WHERE tenant_id = @t", t.TenantId));
        await new SubscriptionPeriodEndJob(fx.Factory.Services.GetRequiredService<ControlDataSource>(), new FakeTimeProvider(periodEnd))
            .EndDueAsync(CancellationToken.None);
        Assert.Equal("Cancelled", await ScalarAsync("SELECT state::text FROM cp.subscription WHERE tenant_id = @t", t.TenantId));
        await (await t.Owner.PostEmptyAsync("/api/v1/saas/subscription/resume", $"\"{await RevisionAsync(t)}\""))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'SubscriptionResumed'", t.TenantId));
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'SubscriptionCancellationRequested'", t.TenantId));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM cp.invoice WHERE tenant_id = @t", t.TenantId));
    }

    // ---- Helpers ----

    private sealed record Arranged(HttpClient Owner, Guid TenantId, Guid PriceId, string SubscriptionRef);

    private async Task<Arranged> ActiveAsync(string label, string[] features, long limitEach)
    {
        var tenant = await TenantProvisioning.SignUpAsync(fx, label);
        var owner = fx.Client(tenant.Admin, tenant.TenantId);
        var price = await CheckoutTests.PublishedPriceAsync(fx, 2500, features: features, limitEach: limitEach);
        var digest = (string)(await (await owner.PostJsonAsync("/api/v1/saas/checkout/preview", new { priceVersionId = price })).ExpectAsync(HttpStatusCode.OK))["previewDigest"]!;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saas/checkout")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { priceVersionId = price, acceptedPreviewDigest = digest }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var session = await (await owner.SendAsync(request)).ExpectAsync(HttpStatusCode.Created);
        await DeliverAndProcessAsync(Simulator.PayCheckout(Guid.Parse((string)session["checkoutId"]!)));
        var subscriptionRef = (string)(await ScalarAsync("SELECT provider_subscription_ref FROM cp.subscription WHERE tenant_id = @t", tenant.TenantId))!;
        return new Arranged(owner, tenant.TenantId, price, subscriptionRef);
    }

    private async Task MedicalStockOnHandAsync(Arranged t)
    {
        async Task<Guid> Create(string path, object body) =>
            Guid.Parse((string)(await (await t.Owner.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!);
        var warehouse = await Create("/api/v1/warehouses", new { code = "MW1", name = "Medical store", timeZone = "UTC" });
        var zone = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "MZ", name = "Ambient" });
        var bin = await Create("/api/v1/bins", new { zoneId = zone, code = "MB1", kind = "Storage" });
        var unit = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        var product = await Create("/api/v1/products",
            new { sku = "MED-1", name = "Synthetic medical item", handlingProfile = "Medical", trackingMode = "Lot", baseUomId = unit, basePrecision = 0 });
        var lot = (Guid)(await ScalarAsync("""
            INSERT INTO ti.lot (tenant_id, product_id, handling_profile, lot_number, expiry_date, created_by)
            VALUES (@t, @p, 'Medical', 'LOT-SYN-1', current_date + 365, 'test') RETURNING id
            """, t.TenantId, ("p", product)))!;
        await TenantProvisioning.ExecAsSuperuserAsync(fx, """
            INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, lot_id, quality_status, received_at, on_hand_qty, created_by)
            VALUES (@t, @w, @b, @p, @l, 'Available', now(), 5, 'test')
            """, ("t", t.TenantId), ("w", warehouse), ("b", bin), ("p", product), ("l", lot));
    }

    private static async Task<JsonObject> PreviewAsync(Arranged t, Guid price) =>
        await (await t.Owner.PostJsonAsync("/api/v1/saas/subscription/change/preview", new { targetPriceVersionId = price })).ExpectAsync(HttpStatusCode.OK);

    private static Task<HttpResponseMessage> ChangeAsync(Arranged t, Guid key, Guid price, string digest, long? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saas/subscription/change")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { targetPriceVersionId = price, acceptedPreviewDigest = digest }),
        };
        request.Headers.Add("Idempotency-Key", key.ToString());
        if (ifMatch is { } v)
            request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{v}\""));
        return t.Owner.SendAsync(request);
    }

    private static async Task<Guid> ScheduleAsync(Arranged t, Guid price)
    {
        var digest = (string)(await PreviewAsync(t, price))["previewDigest"]!;
        var change = await (await ChangeAsync(t, Guid.NewGuid(), price, digest, await RevisionAsync(t))).ExpectAsync(HttpStatusCode.Accepted);
        Assert.Equal("Scheduled", (string?)change["state"]);
        return Guid.Parse((string)change["id"]!);
    }

    private static async Task<long> RevisionAsync(Arranged t) =>
        (long)(await (await t.Owner.GetAsync("/api/v1/saas/subscription")).ExpectAsync(HttpStatusCode.OK))["version"]!;

    private async Task<DateTimeOffset> PeriodEndAsync(Arranged t) =>
        new(DateTime.SpecifyKind((DateTime)(await ScalarAsync("SELECT period_end FROM cp.subscription WHERE tenant_id = @t", t.TenantId))!, DateTimeKind.Utc));

    private async Task<SubscriptionChangeState> ApplyAsync(Guid changeId, DateTimeOffset at)
    {
        await using var app = DependencyInjection.BuildDataSource(fx.AppConnection);
        await using var platform = DependencyInjection.BuildDataSource(fx.PlatformConnection);
        var clock = new FakeTimeProvider(at);
        var runner = new TenantJobRunner(new PlatformDataSource(platform), new AppDataSource(app), clock);
        var job = new PlanChangeJob(fx.Factory.Services.GetRequiredService<ControlDataSource>(), runner, Simulator, clock, NullLogger<PlanChangeJob>.Instance);
        return await job.ApplyAsync(changeId, CancellationToken.None);
    }

    private async Task DeliverAndProcessAsync(SimEvent e)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/billing/simulator") { Content = new ByteArrayContent(e.Body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(SimulatedBillingProvider.SignatureHeader, e.Signature);
        await (await fx.Anonymous().SendAsync(request)).ExpectAsync(HttpStatusCode.OK);
        await fx.Factory.Services.GetRequiredService<BillingEventJob>().RunOnceAsync(CancellationToken.None);
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
