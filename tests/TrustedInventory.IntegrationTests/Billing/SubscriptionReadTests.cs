using System.Net;
using Npgsql;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Billing;

/// <summary>Tenant billing reads (BIL-001, BIL-006, ENT-003, LFC-001, DEC-062): own tenant only, money as integer minor units.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class SubscriptionReadTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "BIL-006")]
    [Trait("Requirement", "ENT-003")]
    [Trait("Requirement", "LFC-001")]
    public async Task The_owner_reads_the_trial_subscription_with_limits_usage_and_access_mode()
    {
        var tenant = await TenantProvisioning.SignUpAsync(fx, "sub-read");
        var owner = fx.Client(tenant.Admin, tenant.TenantId);
        var response = await owner.GetAsync("/api/v1/saas/subscription");
        var body = await response.ExpectAsync(HttpStatusCode.OK);

        Assert.Equal("Trialing", (string?)body["state"]);
        Assert.Equal($"\"{(int)body["version"]!}\"", response.ETag());
        Assert.Equal(TenantProvisioning.TestPlan, (string?)body["planCode"]);
        Assert.Equal(fx.Seed.UnlimitedPlanId.ToString(), (string?)body["planVersionId"]);
        Assert.Null(body["priceVersionId"]);
        Assert.Null(body["interval"]);
        Assert.NotNull(body["trialEnd"]);
        Assert.Null(body["graceEnd"]);
        Assert.False((bool)body["cancelAtPeriodEnd"]!);
        Assert.Null(body["cancellationEffectiveAt"]);
        Assert.Null(body["scheduledChange"]);
        Assert.Equal("Normal", (string?)body["accessMode"]);
        Assert.Empty(body["restrictionReasons"]!.AsArray());
        Assert.Equal(1L, (long)body["usage"]!["active_seats"]!);
        Assert.True((long)body["limits"]!["active_seats"]! >= 1);
    }

    [Fact]
    [Trait("Requirement", "BIL-006")]
    public async Task Billing_reads_are_for_owners_and_billing_administrators_only()
    {
        // A tenant administrator manages the team but holds no billing permission (docs/permissions.md).
        var admin = fx.Client(fx.Seed.A.Admin, fx.Seed.A.TenantId);
        await (await admin.GetAsync("/api/v1/saas/subscription")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await admin.GetAsync("/api/v1/saas/invoices")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    [Trait("Requirement", "BIL-001")]
    [Trait("Requirement", "BIL-006")]
    [Trait("Requirement", "TEN-002")]
    public async Task Invoices_show_snapshot_lines_in_minor_units_with_the_latest_payment_and_only_for_their_own_tenant()
    {
        var mine = await TenantProvisioning.SignUpAsync(fx, "inv-mine");
        var other = await TenantProvisioning.SignUpAsync(fx, "inv-other");
        var t0 = DateTimeOffset.UtcNow.AddDays(-40);
        var older = await InvoiceAsync(mine.TenantId, "Paid", t0, payments: ["Failed", "Succeeded"]);
        var newer = await InvoiceAsync(mine.TenantId, "Open", t0.AddDays(30), payments: ["Failed"]);
        var foreign = await InvoiceAsync(other.TenantId, "Open", t0.AddDays(30), payments: []);

        var owner = fx.Client(mine.Admin, mine.TenantId);
        var list = await (await owner.GetAsync("/api/v1/saas/invoices")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal([newer.ToString(), older.ToString()], list["items"]!.AsArray().Select(i => (string)i!["id"]!));

        var paid = await (await owner.GetAsync($"/api/v1/saas/invoices/{older}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Paid", (string?)paid["status"]);
        Assert.Equal("Succeeded", (string?)paid["paymentStatus"]);
        Assert.Equal(1000L, (long)paid["subtotal"]!["minorUnits"]!);
        Assert.Equal(75L, (long)paid["tax"]!["minorUnits"]!);
        Assert.Equal(1075L, (long)paid["total"]!["minorUnits"]!);
        Assert.Equal("XTS", (string?)paid["total"]!["currency"]);
        Assert.Equal(2, (int)paid["total"]!["exponent"]!);
        var line = paid["lines"]!.AsArray().Single()!;
        Assert.Equal("Fixture plan (monthly)", (string?)line["description"]);
        Assert.Equal(1, (int)line["quantity"]!);
        Assert.Equal(1000L, (long)line["unitAmount"]!["minorUnits"]!);
        Assert.Equal(1075L, (long)line["total"]!["minorUnits"]!);
        Assert.NotNull(paid["paidAt"]);

        var open = await (await owner.GetAsync($"/api/v1/saas/invoices/{newer}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Failed", (string?)open["paymentStatus"]);
        Assert.Null(open["paidAt"]);

        var filtered = await (await owner.GetAsync("/api/v1/saas/invoices?status=Paid")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal([older.ToString()], filtered["items"]!.AsArray().Select(i => (string)i!["id"]!));

        var page = await (await owner.GetAsync("/api/v1/saas/invoices?limit=1")).ExpectAsync(HttpStatusCode.OK);
        var next = await (await owner.GetAsync($"/api/v1/saas/invoices?limit=1&cursor={Uri.EscapeDataString((string)page["nextCursor"]!)}"))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(older.ToString(), (string?)next["items"]!.AsArray().Single()!["id"]);
        Assert.Null((string?)next["nextCursor"]);

        // Another tenant's invoice is simply not there (RLS), with the same answer as a missing id.
        await (await owner.GetAsync($"/api/v1/saas/invoices/{foreign}")).ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await owner.GetAsync($"/api/v1/saas/invoices/{Guid.NewGuid()}")).ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    [Trait("Requirement", "ENT-001")]
    [Trait("Requirement", "TEN-002")]
    public async Task A_contract_only_plan_is_visible_to_its_subscriber_and_to_no_other_tenant()
    {
        var subscriber = await TenantProvisioning.SignUpAsync(fx, "contract-plan");
        var outsider = await TenantProvisioning.SignUpAsync(fx, "contract-outsider");
        var code = "contract-" + Guid.NewGuid().ToString("N")[..10];
        await using var su = new NpgsqlConnection(fx.SuperuserConnection);
        await su.OpenAsync();
        var planId = Guid.CreateVersion7();
        await Exec(su, """
            INSERT INTO cp.plan_version (id, plan_code, version, display_name, features, quotas, is_public, published_at, created_by)
            SELECT @p, @code, 1, 'Contract plan', features, quotas, false, now(), 'seed' FROM cp.plan_version WHERE id = @base
            """, ("p", planId), ("code", code), ("base", fx.Seed.UnlimitedPlanId));
        await Exec(su, "UPDATE cp.subscription SET plan_version_id = @p, revision = revision + 1 WHERE tenant_id = @t",
            ("p", planId), ("t", subscriber.TenantId));

        var body = await (await fx.Client(subscriber.Admin, subscriber.TenantId).GetAsync("/api/v1/saas/subscription")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(code, (string?)body["planCode"]);

        // Directly as ti_app: the outsider's context cannot see the contract-only plan; the subscriber's can.
        Assert.Equal(0L, await VisiblePlanCountAsync(outsider.TenantId, planId));
        Assert.Equal(1L, await VisiblePlanCountAsync(subscriber.TenantId, planId));
        Assert.Equal(0L, await VisiblePlanCountAsync(null, planId));
    }

    private async Task<long> VisiblePlanCountAsync(Guid? tenantId, Guid planId)
    {
        await using var conn = new NpgsqlConnection(fx.AppConnection);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", tenantId?.ToString() ?? "");
            await set.ExecuteNonQueryAsync();
        }
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM cp.plan_version WHERE id = @p", conn, tx);
        cmd.Parameters.AddWithValue("p", planId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<Guid> InvoiceAsync(Guid tenantId, string status, DateTimeOffset periodStart, string[] payments)
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        var id = Guid.CreateVersion7();
        var paidAt = status == "Paid" ? (object)periodStart.AddHours(1) : DBNull.Value;
        await Exec(conn, """
            INSERT INTO cp.invoice (id, tenant_id, billing_account_id, provider, provider_invoice_ref, number, currency, exponent, lines,
                                    subtotal, tax, total, period_start, period_end, status, issued_at, due_at, paid_at, created_at)
            SELECT @id, @t, a.id, 'simulator', @ref, @num, 'XTS', 2,
                   '[{"description":"Fixture plan (monthly)","quantity":1,"unitAmount":1000,"discount":0,"tax":75,"subtotal":1000,"total":1075}]'::jsonb,
                   1000, 75, 1075, @start, @end, @status::cp.invoice_status, @start, @start, @paid, @start
            FROM cp.billing_account a WHERE a.tenant_id = @t
            """, ("id", id), ("t", tenantId), ("ref", "sim_in_" + id.ToString("N")), ("num", "INV-" + id.ToString("N")[..8]),
            ("start", periodStart), ("end", periodStart.AddMonths(1)), ("status", status), ("paid", paidAt));
        var at = periodStart;
        foreach (var payment in payments)
        {
            at = at.AddMinutes(10);
            await Exec(conn, """
                INSERT INTO cp.payment (tenant_id, invoice_id, provider, provider_payment_ref, status, currency, exponent, amount, provider_occurred_at)
                VALUES (@t, @i, 'simulator', @ref, @s::cp.payment_status, 'XTS', 2, 1075, @at)
                """, ("t", tenantId), ("i", id), ("ref", "sim_py_" + Guid.NewGuid().ToString("N")), ("s", payment), ("at", at));
        }
        return id;
    }

    private static async Task<int> Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteNonQueryAsync();
    }
}
