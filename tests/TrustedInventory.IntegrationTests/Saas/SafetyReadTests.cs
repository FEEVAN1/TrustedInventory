using System.Net;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Saas;

/// <summary>Recalls, alerts and lot traceability (same category) stay readable whatever the plan or billing state (SUI-003, ENT-006, DEC-097).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class SafetyReadTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "SUI-003")]
    [Trait("Requirement", "ENT-006")]
    [Trait("Requirement", "LFC-001")]
    public async Task Recalls_and_alerts_are_readable_without_any_entitlement_while_billing_restricted()
    {
        var tenant = await TenantProvisioning.SignUpAsync(fx, "safety-read");
        var owner = fx.Client(tenant.Admin, tenant.TenantId);
        await TenantProvisioning.GrantSelfAsync(fx, tenant, "WarehouseManager"); // alerts.read; granted before the restriction (team admin stops)
        // No feature at all, and the trial over: the workspace is billing-restricted and entitled to nothing (as when a trial is withheld).
        await TenantProvisioning.ExecAsSuperuserAsync(fx, """
            UPDATE cp.subscription SET state = 'Trialing', price_version_id = NULL, period_start = NULL, period_end = NULL,
                   trial_start = now() - interval '15 days', trial_end = now() - interval '1 day'
            WHERE tenant_id = @t
            """, ("t", tenant.TenantId));
        await TenantProvisioning.ExecAsSuperuserAsync(fx, "UPDATE cp.entitlement_snapshot SET features = '{}' WHERE tenant_id = @t AND is_current",
            ("t", tenant.TenantId));
        Assert.Equal("BillingRestricted", (string?)(await (await owner.GetAsync("/api/v1/saas/subscription")).ExpectAsync(HttpStatusCode.OK))["accessMode"]);

        // Ordinary reads need the feature and are refused by name; safety reads are not.
        var refused = await (await owner.GetAsync("/api/v1/stock")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FEATURE_NOT_ENABLED");
        Assert.Equal("core_inventory", (string?)refused["feature"]);
        await (await owner.GetAsync("/api/v1/recalls")).ExpectAsync(HttpStatusCode.OK);
        await (await owner.GetAsync("/api/v1/alerts")).ExpectAsync(HttpStatusCode.OK);
        await (await owner.GetAsync($"/api/v1/recalls/{Guid.NewGuid()}")).ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await owner.GetAsync($"/api/v1/alerts/{Guid.NewGuid()}")).ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }
}
