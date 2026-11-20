using System.Globalization;
using System.Net;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Billing;

/// <summary>Complimentary access (section 29, DEC-094): requested by platform billing, approved by another operator, revocable, audited.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ComplimentaryAccessTests(ApiFixture fx)
{
    private const string Root = "/platform/v1/billing/complimentary-access";

    [Fact]
    [Trait("Requirement", "BIL-008")]
    [Trait("Requirement", "SOD-8")]
    [Trait("Requirement", "LFC-001")]
    public async Task A_grant_lifts_the_billing_restriction_only_after_another_operator_approves_it_and_ends_when_revoked()
    {
        var tenant = await TenantProvisioning.SignUpAsync(fx, "comp");
        var owner = fx.Client(tenant.Admin, tenant.TenantId);
        await TenantProvisioning.ExecAsSuperuserAsync(fx, """
            UPDATE cp.subscription SET state = 'Trialing', price_version_id = NULL, period_start = NULL, period_end = NULL,
                   trial_start = now() - interval '15 days', trial_end = now() - interval '1 day'
            WHERE tenant_id = @t
            """, ("t", tenant.TenantId));
        await AccessAsync(owner, "BillingRestricted");

        var price = await CheckoutTests.PublishedPriceAsync(fx, 2500, features: ["core_inventory", "advanced_reporting"], limitEach: 7);
        var planVersion = (Guid)(await TenantProvisioning.ScalarAsSuperuserAsync(fx, "SELECT plan_version_id FROM cp.price_version WHERE id = @p", ("p", price)))!;
        var draft = await CheckoutTests.PublishedPriceAsync(fx, 2500, publish: false);
        var draftPlan = (Guid)(await TenantProvisioning.ScalarAsSuperuserAsync(fx, "SELECT plan_version_id FROM cp.price_version WHERE id = @p", ("p", draft)))!;
        var billing = fx.Client(SeedData.PlatformBilling, tenantId: null);
        var admin = fx.Client(SeedData.PlatformAdmin, tenantId: null);
        var expires = DateTimeOffset.UtcNow.AddDays(30);
        object Body(Guid plan, DateTimeOffset until) => new { tenantId = tenant.TenantId, planVersionId = plan, expiresAt = until, reason = "Pilot partner, agreed by sales" };

        // Only platform billing requests; tenant users are not platform callers; limits on term and plan.
        await (await admin.PostJsonAsync(Root, Body(planVersion, expires))).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.True((await owner.PostJsonAsync(Root, Body(planVersion, expires))).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
        await (await billing.PostJsonAsync(Root, Body(planVersion, DateTimeOffset.UtcNow.AddDays(400))))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        await (await billing.PostJsonAsync(Root, Body(draftPlan, expires))).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        await (await billing.PostJsonAsync(Root, new { tenantId = Guid.NewGuid(), planVersionId = planVersion, expiresAt = expires, reason = "x" }))
            .ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");

        var grant = await (await billing.PostJsonAsync(Root, Body(planVersion, expires))).ExpectAsync(HttpStatusCode.Created);
        var id = (string)grant["id"]!;
        Assert.Equal("Pending", (string?)grant["status"]);
        Assert.Equal(SeedData.PlatformBilling, (string?)grant["requestedBy"]);
        // A pending grant changes nothing, and a workspace holds one open grant at a time.
        await AccessAsync(owner, "BillingRestricted");
        await (await billing.PostJsonAsync(Root, Body(planVersion, expires))).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");

        // Independent approval: the requester cannot approve; a stale version is refused; another operator approves.
        await (await billing.PostJsonAsync($"{Root}/{id}/approve", new { reason = "self" }, "\"1\"")).ExpectProblemAsync(HttpStatusCode.Conflict, "APPROVAL_SELF");
        await (await admin.PostJsonAsync($"{Root}/{id}/approve", new { reason = "ok" }, "\"9\"")).ExpectProblemAsync(HttpStatusCode.PreconditionFailed, "VERSION_CONFLICT");
        await (await admin.PostJsonAsync($"{Root}/{id}/approve", new { reason = " " }, "\"1\"")).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        var approved = await (await admin.PostJsonAsync($"{Root}/{id}/approve", new { reason = "Checked the partner agreement" }, "\"1\""))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Active", (string?)approved["status"]);
        Assert.Equal(SeedData.PlatformAdmin, (string?)approved["approvedBy"]);

        var subscription = await AccessAsync(owner, "Normal");
        Assert.Equal(expires.ToUnixTimeSeconds(), DateTimeOffset.Parse((string)subscription["complimentaryUntil"]!, CultureInfo.InvariantCulture).ToUnixTimeSeconds());
        Assert.Equal(7L, (long)subscription["limits"]!["active_warehouses"]!);
        Assert.Equal("Trialing", (string?)subscription["state"]); // the subscription itself is untouched
        await (await owner.PostJsonAsync("/api/v1/warehouses", new { code = "CMP-1", name = "Complimentary", timeZone = "UTC" })).ExpectAsync(HttpStatusCode.Created);

        // Listing by tenant and status; an approved grant cannot be approved again.
        var active = await (await admin.GetAsync($"{Root}?tenantId={tenant.TenantId}&status=Active")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(id, (string?)Assert.Single(active["items"]!.AsArray())!["id"]);
        Assert.Empty((await (await billing.GetAsync($"{Root}?tenantId={tenant.TenantId}&status=Pending")).ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray());
        await (await admin.PostJsonAsync($"{Root}/{id}/approve", new { reason = "again" }, "\"2\"")).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");

        // Revocation ends it at once; the billing restriction returns.
        var revoked = await (await billing.PostJsonAsync($"{Root}/{id}/revoke", new { reason = "Pilot ended early" }, "\"2\"")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Revoked", (string?)revoked["status"]);
        var after = await AccessAsync(owner, "BillingRestricted");
        Assert.Null(after["complimentaryUntil"]);
        await (await billing.PostJsonAsync($"{Root}/{id}/revoke", new { reason = "again" }, "\"3\"")).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");

        // Every step is in the workspace's own audit chain, and the rows cannot be rewritten.
        Assert.Equal(3L, Convert.ToInt64(await TenantProvisioning.ScalarAsSuperuserAsync(fx,
            "SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type LIKE 'ComplimentaryAccess%'", ("t", tenant.TenantId)),
            CultureInfo.InvariantCulture));
        await Assert.ThrowsAnyAsync<Exception>(() => TenantProvisioning.ExecAsSuperuserAsync(fx,
            "UPDATE cp.complimentary_access SET expires_at = expires_at + interval '1 day' WHERE id = @id", ("id", Guid.Parse(id))));

        // A new grant can be requested once the previous one is closed.
        await (await billing.PostJsonAsync(Root, Body(planVersion, expires))).ExpectAsync(HttpStatusCode.Created);
    }

    private static async Task<System.Text.Json.Nodes.JsonNode> AccessAsync(HttpClient owner, string mode)
    {
        var subscription = await (await owner.GetAsync("/api/v1/saas/subscription")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(mode, (string?)subscription["accessMode"]);
        return subscription;
    }
}
