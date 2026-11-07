using System.Net;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Administration;

[Collection(ApiTestGroup.Name)]
public sealed class MembershipTests(ApiFixture fx)
{
    private static string NewSubject() => $"user-new-{Guid.NewGuid():N}";

    private static async Task<(string Id, string Subject)> CreateMemberAsync(HttpClient admin)
    {
        var subject = NewSubject();
        var body = await (await admin.PostJsonAsync("/api/v1/memberships", new { userSubject = subject, displayName = "New Person" }))
            .ExpectAsync(HttpStatusCode.Created);
        return ((string)body["id"]!, subject);
    }

    [Fact]
    [Trait("Requirement", "IAM-001")]
    [Trait("Requirement", "IAM-002")]
    [Trait("Requirement", "AUD-001")]
    public async Task Grants_give_access_and_deactivation_removes_it_immediately()
    {
        var admin = fx.Client(fx.Seed.A.Admin, fx.Seed.A.TenantId);
        var (id, subject) = await CreateMemberAsync(admin);
        var user = fx.Client(subject, fx.Seed.A.TenantId);

        await (await user.GetAsync("/api/v1/warehouses")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        var grant = await admin.PostJsonAsync($"/api/v1/memberships/{id}/role-grants", new { role = "WarehouseManager", reason = "New starter" });
        Assert.Equal("\"1\"", grant.ETag());
        Assert.True((bool)(await grant.ExpectAsync(HttpStatusCode.Created))["active"]!);
        await (await user.GetAsync("/api/v1/warehouses")).ExpectAsync(HttpStatusCode.OK);

        await (await admin.PostJsonAsync($"/api/v1/memberships/{id}/deactivate", new { reason = "Left the company" }, "\"1\""))
            .ExpectAsync(HttpStatusCode.OK);
        await (await user.GetAsync("/api/v1/warehouses")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        var audited = await Organization.TenantProvisioning.ScalarAsSuperuserAsync(fx,
            "SELECT reason FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'MembershipDeactivated' AND object_id = @o",
            ("t", fx.Seed.A.TenantId), ("o", Guid.Parse(id)));
        Assert.Equal("Left the company", audited);
    }

    [Fact]
    [Trait("Requirement", "SOD-3")]
    public async Task Administrators_cannot_grant_QA_signing_to_themselves()
    {
        var admin = fx.Client(fx.Seed.A.Admin, fx.Seed.A.TenantId);
        var memberships = await (await admin.GetAsync("/api/v1/memberships?limit=200")).ExpectAsync(HttpStatusCode.OK);
        var self = memberships["items"]!.AsArray().Single(m => (string?)m!["userSubject"] == fx.Seed.A.Admin)!;

        await (await admin.PostJsonAsync($"/api/v1/memberships/{self["id"]}/role-grants", new { role = "QaOfficer", reason = "self" }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "APPROVAL_SELF");

        var (otherId, _) = await CreateMemberAsync(admin);
        await (await admin.PostJsonAsync($"/api/v1/memberships/{otherId}/role-grants", new { role = "QaOfficer", reason = "Qualified" }))
            .ExpectAsync(HttpStatusCode.Created);
    }

    [Fact]
    [Trait("Requirement", "IAM-001")]
    [Trait("Requirement", "VALIDATION")]
    public async Task Invalid_grants_are_rejected()
    {
        var admin = fx.Client(fx.Seed.A.Admin, fx.Seed.A.TenantId);
        var (id, _) = await CreateMemberAsync(admin);

        await (await admin.PostJsonAsync($"/api/v1/memberships/{id}/role-grants", new { role = "PlatformAdministrator", reason = "x" }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        await (await admin.PostJsonAsync($"/api/v1/memberships/{id}/role-grants",
                new { role = "Picker", warehouseId = fx.Seed.B.ScopedWarehouseId, reason = "other tenant's warehouse" }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");

        await (await admin.PostJsonAsync($"/api/v1/memberships/{id}/role-grants", new { role = "Picker", reason = "first" }))
            .ExpectAsync(HttpStatusCode.Created);
        await (await admin.PostJsonAsync($"/api/v1/memberships/{id}/role-grants", new { role = "Picker", reason = "duplicate" }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");

        var duplicate = await admin.PostJsonAsync("/api/v1/memberships", new { userSubject = fx.Seed.A.Picker });
        await duplicate.ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
    }

    [Fact]
    [Trait("Requirement", "AUD-001")]
    [Trait("Requirement", "API-002")]
    public async Task Revoking_a_grant_is_versioned_and_one_way()
    {
        var admin = fx.Client(fx.Seed.A.Admin, fx.Seed.A.TenantId);
        var (id, subject) = await CreateMemberAsync(admin);
        var grant = await (await admin.PostJsonAsync($"/api/v1/memberships/{id}/role-grants", new { role = "Auditor", reason = "Audit" }))
            .ExpectAsync(HttpStatusCode.Created);

        await (await admin.PostJsonAsync($"/api/v1/role-grants/{grant["id"]}/revoke", new { reason = "Done" }))
            .ExpectProblemAsync((HttpStatusCode)428, "PRECONDITION_REQUIRED");
        var revoked = await admin.PostJsonAsync($"/api/v1/role-grants/{grant["id"]}/revoke", new { reason = "Done" }, "\"1\"");
        var body = await revoked.ExpectAsync(HttpStatusCode.OK);
        Assert.False((bool)body["active"]!);
        Assert.Equal(fx.Seed.A.Admin, (string?)body["revokedBy"]);
        await (await admin.PostJsonAsync($"/api/v1/role-grants/{grant["id"]}/revoke", new { reason = "Again" }, "\"2\""))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");

        var active = await (await admin.GetAsync($"/api/v1/memberships/{id}/role-grants?active=true")).ExpectAsync(HttpStatusCode.OK);
        Assert.Empty(active["items"]!.AsArray());
        await (await fx.Client(subject, fx.Seed.A.TenantId).GetAsync("/api/v1/audit/events"))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    [Trait("Requirement", "AT-TEN-01")]
    [Trait("Requirement", "IAM-001")]
    public async Task Memberships_are_tenant_isolated_and_require_member_management()
    {
        var adminA = fx.Client(fx.Seed.A.Admin, fx.Seed.A.TenantId);
        var adminB = fx.Client(fx.Seed.B.Admin, fx.Seed.B.TenantId);
        var (id, _) = await CreateMemberAsync(adminA);
        var grant = await (await adminA.PostJsonAsync($"/api/v1/memberships/{id}/role-grants", new { role = "Picker", reason = "x" }))
            .ExpectAsync(HttpStatusCode.Created);

        await (await adminB.GetAsync($"/api/v1/memberships/{id}")).ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await adminB.GetAsync($"/api/v1/memberships/{id}/role-grants")).ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await adminB.PostJsonAsync($"/api/v1/role-grants/{grant["id"]}/revoke", new { reason = "x" }, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await adminB.PostJsonAsync($"/api/v1/memberships/{id}/deactivate", new { reason = "x" }, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        var listB = await (await adminB.GetAsync("/api/v1/memberships?limit=200")).ExpectAsync(HttpStatusCode.OK);
        Assert.DoesNotContain(listB["items"]!.AsArray(), m => (string?)m!["id"] == id);

        await (await fx.Client(fx.Seed.A.Picker, fx.Seed.A.TenantId).GetAsync("/api/v1/memberships"))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
    }
}
