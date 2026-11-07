using System.Net;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Organization;

[Collection(ApiTestGroup.Name)]
public sealed class PlatformTenantTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "TEN-004")]
    [Trait("Requirement", "IAM-001")]
    public async Task Only_platform_administrators_reach_platform_routes_and_are_rejected_before_validation()
    {
        var tenantAdmin = fx.Client(fx.Seed.A.Admin, tenantId: null);
        await (await tenantAdmin.GetAsync("/api/v1/platform/tenants")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        // An invalid body still gets 403, not a validation error: authorization runs before binding.
        await (await tenantAdmin.PostJsonAsync("/api/v1/platform/tenants", new { bogus = true }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        var noMfa = fx.Client(SeedData.PlatformAdmin, tenantId: null, mfa: false);
        await (await noMfa.GetAsync("/api/v1/platform/tenants")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    [Trait("Requirement", "TEN-004")]
    [Trait("Requirement", "AUD-001")]
    public async Task Provisioning_creates_tenant_first_administrator_and_audited_history()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "prov");
        var platform = fx.Client(SeedData.PlatformAdmin, tenantId: null);

        var fetched = await platform.GetAsync($"/api/v1/platform/tenants/{tenant.TenantId}");
        var body = await fetched.ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Active", (string?)body["status"]);
        Assert.Equal("\"1\"", fetched.ETag());

        // The first administrator can work immediately.
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        await (await admin.PostJsonAsync("/api/v1/warehouses", new { code = "WH1", name = "First", timeZone = "UTC" }))
            .ExpectAsync(HttpStatusCode.Created);

        var events = await TenantProvisioning.ScalarAsSuperuserAsync(fx, """
            SELECT string_agg(event_type || ':' || actor_subject, ',' ORDER BY sequence) FROM ti.audit_event
            WHERE tenant_id = @t AND sequence <= 3
            """, ("t", tenant.TenantId));
        Assert.Equal($"TenantCreated:{SeedData.PlatformAdmin},MembershipCreated:{SeedData.PlatformAdmin},RoleGranted:{SeedData.PlatformAdmin}", events);

        var list = await (await platform.GetAsync("/api/v1/platform/tenants?status=Active&limit=200")).ExpectAsync(HttpStatusCode.OK);
        Assert.Contains(list["items"]!.AsArray(), t => (string?)t!["id"] == tenant.TenantId.ToString());
    }

    [Fact]
    [Trait("Requirement", "TEN-004")]
    public async Task Platform_administrators_have_no_tenant_data_access()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "noaccess");
        var platformInTenant = fx.Client(SeedData.PlatformAdmin, tenant.TenantId);
        await (await platformInTenant.GetAsync("/api/v1/warehouses")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    [Trait("Requirement", "SEC-SUPPORT")]
    [Trait("Requirement", "API-002")]
    public async Task Suspension_blocks_mutations_but_not_reads_until_reactivated()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "suspend");
        var platform = fx.Client(SeedData.PlatformAdmin, tenantId: null);
        var admin = fx.Client(tenant.Admin, tenant.TenantId);

        await (await platform.PostJsonAsync($"/api/v1/platform/tenants/{tenant.TenantId}/suspend", new { reason = "Unpaid" }))
            .ExpectProblemAsync((HttpStatusCode)428, "PRECONDITION_REQUIRED");
        var suspended = await platform.PostJsonAsync($"/api/v1/platform/tenants/{tenant.TenantId}/suspend", new { reason = "Contract review" }, "\"1\"");
        var body = await suspended.ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Suspended", (string?)body["status"]);
        Assert.Equal("Contract review", (string?)body["suspendedReason"]);
        Assert.Equal("\"2\"", suspended.ETag());

        await (await admin.PostJsonAsync("/api/v1/warehouses", new { code = "WH-S", name = "Blocked", timeZone = "UTC" }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await admin.GetAsync("/api/v1/warehouses")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Contract review",
            (string?)(await (await platform.GetAsync($"/api/v1/platform/tenants/{tenant.TenantId}")).ExpectAsync(HttpStatusCode.OK))["suspendedReason"]);

        await (await platform.PostJsonAsync($"/api/v1/platform/tenants/{tenant.TenantId}/suspend", new { reason = "again" }, "\"2\""))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        var reactivated = await platform.PostJsonAsync($"/api/v1/platform/tenants/{tenant.TenantId}/reactivate", new { reason = "Resolved" }, "\"2\"");
        Assert.Null((await reactivated.ExpectAsync(HttpStatusCode.OK))["suspendedReason"]?.GetValue<string>());
        await (await admin.PostJsonAsync("/api/v1/warehouses", new { code = "WH-S", name = "Allowed", timeZone = "UTC" }))
            .ExpectAsync(HttpStatusCode.Created);
    }
}
