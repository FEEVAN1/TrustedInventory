using System.Net;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Organization;

/// <summary>Platform operations console on /platform/v1 (SAS-003, SAS-004, DEC-026, DEC-037).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class PlatformTenantTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "TEN-004")]
    [Trait("Requirement", "IAM-001")]
    [Trait("Requirement", "SAS-003")]
    public async Task Only_platform_administrators_reach_platform_routes_and_are_rejected_before_validation()
    {
        var tenantAdmin = fx.Client(fx.Seed.A.Admin, tenantId: null);
        await (await tenantAdmin.GetAsync("/platform/v1/tenants")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        // An invalid body still gets 403, not a validation error: authorization runs before binding.
        await (await tenantAdmin.PostJsonAsync($"/platform/v1/tenants/{fx.Seed.A.TenantId}/security-suspension", new { bogus = true }, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        // Billing operators are a separate list (DEC-036): they are not operations administrators.
        var billing = fx.Client(SeedData.PlatformBilling, tenantId: null);
        await (await billing.GetAsync("/platform/v1/tenants")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        var noMfa = fx.Client(SeedData.PlatformAdmin, tenantId: null, mfa: false);
        await (await noMfa.GetAsync("/platform/v1/tenants")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        await (await fx.Anonymous().GetAsync("/platform/v1/tenants")).ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");

        // The v2.0 routes are gone (DEC-037): tenants are created only by signup.
        var platform = fx.Client(SeedData.PlatformAdmin, tenantId: null);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.GetAsync("/api/v1/platform/tenants")).StatusCode);
    }

    [Fact]
    [Trait("Requirement", "SAS-004")]
    [Trait("Requirement", "SOP-006")]
    public async Task Platform_tenant_view_is_service_metadata_with_computed_access()
    {
        var platform = fx.Client(SeedData.PlatformAdmin, tenantId: null);
        var fetched = await platform.GetAsync($"/platform/v1/tenants/{fx.Seed.A.TenantId}");
        var body = await fetched.ExpectAsync(HttpStatusCode.OK);
        var tenant = body["tenant"]!;
        Assert.Equal("Ready", (string?)tenant["lifecycle"]);
        Assert.Equal("Normal", (string?)tenant["accessMode"]);
        Assert.Empty(tenant["restrictionReasons"]!.AsArray());
        Assert.Null(tenant["securitySuspension"]);
        Assert.Equal("Active", (string?)body["subscriptionState"]);
        Assert.Equal(TenantProvisioning.TestPlan, (string?)body["planCode"]);
        Assert.Equal($"\"{(int)tenant["version"]!}\"", fetched.ETag());

        // Only metadata: no stock, catalog or billing documents in the platform view.
        var text = body.ToJsonString();
        foreach (var leak in new[] { "warehouse", "product", "invoice", "billingContact", "taxReference" })
            Assert.DoesNotContain(leak, text, StringComparison.OrdinalIgnoreCase);

        var list = await (await platform.GetAsync("/platform/v1/tenants?lifecycle=Ready&accessMode=Normal&limit=200")).ExpectAsync(HttpStatusCode.OK);
        Assert.Contains(list["items"]!.AsArray(), t => (string?)t!["tenant"]!["id"] == fx.Seed.A.TenantId.ToString());
        await (await platform.GetAsync($"/platform/v1/tenants/{Guid.NewGuid()}")).ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
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
    [Trait("Requirement", "LFC-001")]
    [Trait("Requirement", "AUD-001")]
    [Trait("Requirement", "API-002")]
    public async Task Security_suspension_blocks_tenant_access_except_identity_until_lifted()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "suspend");
        var platform = fx.Client(SeedData.PlatformAdmin, tenantId: null);
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        var url = $"/platform/v1/tenants/{tenant.TenantId}";

        await (await platform.PostJsonAsync($"{url}/security-suspension", new { reason = "Credential leak review" }))
            .ExpectProblemAsync((HttpStatusCode)428, "PRECONDITION_REQUIRED");
        var suspended = await platform.PostJsonAsync($"{url}/security-suspension", new { reason = "Credential leak review" }, "\"1\"");
        var body = await suspended.ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("SecuritySuspended", (string?)body["tenant"]!["accessMode"]);
        Assert.Equal("Credential leak review", (string?)body["tenant"]!["securitySuspension"]!["reason"]);
        Assert.Equal(SeedData.PlatformAdmin, (string?)body["tenant"]!["securitySuspension"]!["by"]);
        Assert.Equal("\"2\"", suspended.ETag());

        // Reads and writes are blocked; identity still answers and shows the restriction (section 26).
        var write = await (await admin.PostJsonAsync("/api/v1/warehouses", new { code = "WH-S", name = "Blocked", timeZone = "UTC" }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "TENANT_SECURITY_SUSPENDED");
        Assert.Contains("SecuritySuspended", write["recovery"]!["restrictionReasons"]!.AsArray().Select(r => (string?)r));
        await (await admin.GetAsync("/api/v1/warehouses")).ExpectProblemAsync(HttpStatusCode.Forbidden, "TENANT_SECURITY_SUSPENDED");
        var me = await (await fx.Client(tenant.Admin, tenantId: null).GetAsync("/api/v1/me")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("SecuritySuspended", (string?)me["memberships"]!.AsArray().Single()!["accessMode"]);

        await (await platform.PostJsonAsync($"{url}/security-suspension", new { reason = "again" }, "\"2\""))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        var lifted = await platform.PostJsonAsync($"{url}/security-suspension/lift", new { reason = "Review closed" }, "\"2\"");
        Assert.Null((await lifted.ExpectAsync(HttpStatusCode.OK))["tenant"]!["securitySuspension"]);
        await (await admin.PostJsonAsync("/api/v1/warehouses", new { code = "WH-S", name = "Allowed", timeZone = "UTC" }))
            .ExpectAsync(HttpStatusCode.Created);

        var events = await TenantProvisioning.ScalarAsSuperuserAsync(fx, """
            SELECT string_agg(event_type || ':' || actor_subject || ':' || coalesce(reason, ''), ',' ORDER BY sequence) FROM ti.audit_event
            WHERE tenant_id = @t AND object_type = 'Tenant'
            """, ("t", tenant.TenantId));
        Assert.Equal($"TenantSecuritySuspended:{SeedData.PlatformAdmin}:Credential leak review,"
            + $"TenantSecuritySuspensionLifted:{SeedData.PlatformAdmin}:Review closed", events);
    }

    [Fact]
    [Trait("Requirement", "ONB-002")]
    [Trait("Requirement", "SOP-003")]
    public async Task Failed_provisioning_jobs_are_listed_and_retry_resets_them()
    {
        var jobId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        await TenantProvisioning.ExecAsSuperuserAsync(fx, """
            INSERT INTO cp.provisioning_job (id, signup_key, requested_by_subject, request_digest, request, tenant_id, status, current_step,
                                             failure_code, attempts)
            VALUES (@id, @key, 'user-signup-test', decode(repeat('ab', 32), 'hex'), '{}'::jsonb, @t, 'Failed', 'CreateBillingAccount',
                    'TEST_FAILURE', 3)
            """, ("id", jobId), ("key", "test:" + jobId), ("t", tenantId));
        var platform = fx.Client(SeedData.PlatformAdmin, tenantId: null);

        var failed = await (await platform.GetAsync("/platform/v1/provisioning-jobs?status=Failed&limit=200")).ExpectAsync(HttpStatusCode.OK);
        var job = failed["items"]!.AsArray().Single(j => (string?)j!["id"] == jobId.ToString())!;
        Assert.True((bool)job["retryable"]!);
        Assert.Equal("TEST_FAILURE", (string?)job["errorCode"]);

        await (await platform.PostEmptyAsync($"/platform/v1/provisioning-jobs/{jobId}/retry"))
            .ExpectProblemAsync(HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_REQUIRED");
        var retry = new HttpRequestMessage(HttpMethod.Post, $"/platform/v1/provisioning-jobs/{jobId}/retry");
        retry.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var reset = await (await platform.SendAsync(retry)).ExpectAsync(HttpStatusCode.Accepted);
        Assert.Equal("Pending", (string?)reset["status"]);
        Assert.Null((string?)reset["errorCode"]);
    }
}
