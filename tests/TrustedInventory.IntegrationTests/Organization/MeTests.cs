using System.Net;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Organization;

[Collection(ApiTestGroup.Name)]
public sealed class MeTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "IAM-002")]
    [Trait("Requirement", "API-001")]
    public async Task Me_lists_only_the_callers_active_memberships_and_scoped_permissions()
    {
        var me = await (await fx.Client(fx.Seed.A.ScopedManager, tenantId: null).GetAsync("/api/v1/me")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(fx.Seed.A.ScopedManager, (string?)me["subject"]);
        Assert.NotNull((string?)me["authTime"]);

        var membership = Assert.Single(me["memberships"]!.AsArray())!;
        Assert.Equal(fx.Seed.A.TenantId.ToString(), (string?)membership["tenantId"]);
        var permissions = membership["permissions"]!.AsArray();
        var scoped = permissions.Single(p => (string?)p!["permission"] == "warehouses.manage")!;
        Assert.Equal(fx.Seed.A.ScopedWarehouseId.ToString(), (string?)Assert.Single(scoped["warehouseIds"]!.AsArray()));
        var tenantWide = permissions.Single(p => (string?)p!["permission"] == "catalog.manage")!;
        Assert.Null(tenantWide["warehouseIds"]);

        // Workspace switcher fields (section 29): computed access, lifecycle, slug and plan.
        Assert.Equal("Normal", (string?)membership["accessMode"]);
        Assert.Equal("Ready", (string?)membership["lifecycle"]);
        Assert.Empty(membership["restrictionReasons"]!.AsArray());
        Assert.False(string.IsNullOrEmpty((string?)membership["tenantSlug"]));
        Assert.Equal(TenantProvisioning.TestPlan, (string?)membership["planCode"]);

        await (await fx.Anonymous().GetAsync("/api/v1/me")).ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }
}
