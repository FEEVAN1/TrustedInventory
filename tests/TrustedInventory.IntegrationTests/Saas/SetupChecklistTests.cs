using System.Net;
using System.Text.Json.Nodes;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Saas;

/// <summary>
/// ONB-004 setup checklist (DEC-046): computed from records on every read, visible to any member once Ready, and
/// side-effect free. Sample data is deferred to M2.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class SetupChecklistTests(ApiFixture fx)
{
    private const string Path = "/api/v1/saas/setup-checklist";

    [Fact]
    [Trait("Requirement", "ONB-004")]
    public async Task Items_complete_as_the_workspace_is_set_up_and_system_records_never_count()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "setup-progress");
        var admin = fx.Client(tenant.Admin, tenant.TenantId);

        var start = await ChecklistAsync(admin);
        Assert.Equal(["business-profile", "warehouse-timezone", "zones-bins", "products-units", "team-invitations", "import"],
            start["items"]!.AsArray().Select(i => (string)i!["key"]!));
        AssertStatuses(start, "Done", "Pending", "Pending", "Pending", "Pending", "Unavailable");
        Assert.False((bool)Item(start, "import")["required"]!);
        Assert.Equal(1, (int)start["requiredCompleted"]!);
        Assert.Equal(5, (int)start["requiredTotal"]!);
        Assert.Null(start["trialEnd"]); // active paid subscription

        // A warehouse brings its system zone and Transit bin; neither counts as setting up zones and bins.
        var warehouse = (string)(await (await admin.PostJsonAsync("/api/v1/warehouses", new { code = "SET-1", name = "Main", timeZone = "Africa/Lagos" }))
            .ExpectAsync(HttpStatusCode.Created))["id"]!;
        AssertStatuses(await ChecklistAsync(admin), "Done", "Done", "Pending", "Pending", "Pending", "Unavailable");

        var zone = (string)(await (await admin.PostJsonAsync("/api/v1/zones", new { warehouseId = warehouse, code = "Z1", name = "Ambient" }))
            .ExpectAsync(HttpStatusCode.Created))["id"]!;
        AssertStatuses(await ChecklistAsync(admin), "Done", "Done", "Pending", "Pending", "Pending", "Unavailable"); // zone without a bin
        await (await admin.PostJsonAsync("/api/v1/bins", new { zoneId = zone, code = "B1", kind = "Storage" })).ExpectAsync(HttpStatusCode.Created);
        AssertStatuses(await ChecklistAsync(admin), "Done", "Done", "Done", "Pending", "Pending", "Unavailable");

        var unit = (string)(await (await admin.PostJsonAsync("/api/v1/units", new { code = "EA", name = "Each" })).ExpectAsync(HttpStatusCode.Created))["id"]!;
        AssertStatuses(await ChecklistAsync(admin), "Done", "Done", "Done", "Pending", "Pending", "Unavailable"); // a unit alone is not a catalog
        await (await admin.PostJsonAsync("/api/v1/products", new
        {
            sku = "SET-SKU", name = "Widget", handlingProfile = "General", trackingMode = "Quantity", baseUomId = unit, basePrecision = 0,
        })).ExpectAsync(HttpStatusCode.Created);
        AssertStatuses(await ChecklistAsync(admin), "Done", "Done", "Done", "Done", "Pending", "Unavailable");

        await (await admin.PostJsonAsync("/api/v1/invitations",
            new { email = $"setup-{Guid.NewGuid():N}@example.invalid", roles = new[] { new { role = "Picker" } } })).ExpectAsync(HttpStatusCode.Created);
        var done = await ChecklistAsync(admin);
        AssertStatuses(done, "Done", "Done", "Done", "Done", "Done", "Unavailable");
        Assert.Equal(5, (int)done["requiredCompleted"]!);
    }

    [Fact]
    [Trait("Requirement", "ONB-004")]
    public async Task Reading_the_checklist_changes_nothing()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "setup-readonly");
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        var before = await FootprintAsync(tenant.TenantId);

        for (var i = 0; i < 3; i++)
            await ChecklistAsync(admin);

        // No records, audit events, outbox events or usage counters appear from reading (and no stock can exist in S1).
        Assert.Equal(before, await FootprintAsync(tenant.TenantId));
    }

    [Fact]
    [Trait("Requirement", "ONB-004")]
    [Trait("Requirement", "LFC-001")]
    public async Task Any_member_reads_it_once_ready_outsiders_and_provisioning_workspaces_do_not()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "setup-access");
        var noRoles = $"user-setup-member-{Guid.NewGuid():N}";
        await TenantProvisioning.ExecAsSuperuserAsync(fx,
            "INSERT INTO ti.membership (tenant_id, user_subject, created_by) VALUES (@t, @s, 'seed')", ("t", tenant.TenantId), ("s", noRoles));

        await ChecklistAsync(fx.Client(noRoles, tenant.TenantId));
        await (await fx.Client(fx.Seed.B.Admin, tenant.TenantId).GetAsync(Path)).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        // Reading stays available while billing-restricted (read category).
        await TenantProvisioning.ExecAsSuperuserAsync(fx, """
            UPDATE cp.subscription SET state = 'Trialing', price_version_id = NULL, period_start = NULL, period_end = NULL,
                   trial_start = now() - interval '15 days', trial_end = now() - interval '1 day'
            WHERE tenant_id = @t
            """, ("t", tenant.TenantId));
        await ChecklistAsync(fx.Client(noRoles, tenant.TenantId));

        // Before Ready there is no checklist: the workspace is still being set up.
        await TenantProvisioning.ExecAsSuperuserAsync(fx, "UPDATE ti.tenant SET lifecycle = 'Provisioning' WHERE id = @t", ("t", tenant.TenantId));
        await (await fx.Client(tenant.Admin, tenant.TenantId).GetAsync(Path)).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
    }

    [Fact]
    [Trait("Requirement", "ONB-004")]
    [Trait("Requirement", "ONB-003")]
    public async Task A_trialing_workspace_shows_its_trial_end()
    {
        var tenant = await TenantProvisioning.SignUpAsync(fx, "setup-trial");
        var checklist = await ChecklistAsync(fx.Client(tenant.Admin, tenant.TenantId));

        var stored = (DateTime)(await TenantProvisioning.ScalarAsSuperuserAsync(fx,
            "SELECT trial_end FROM cp.subscription WHERE tenant_id = @t", ("t", tenant.TenantId)))!;
        Assert.Equal(new DateTimeOffset(stored, TimeSpan.Zero), DateTimeOffset.Parse((string)checklist["trialEnd"]!, System.Globalization.CultureInfo.InvariantCulture));
        // Signup recorded the business profile; the owner is the only member and nothing else is set up yet.
        AssertStatuses(checklist, "Done", "Pending", "Pending", "Pending", "Pending", "Unavailable");
    }

    private static async Task<JsonObject> ChecklistAsync(HttpClient client) =>
        await (await client.GetAsync(Path)).ExpectAsync(HttpStatusCode.OK);

    private static JsonObject Item(JsonObject checklist, string key) =>
        checklist["items"]!.AsArray().Single(i => (string?)i!["key"] == key)!.AsObject();

    private static void AssertStatuses(JsonObject checklist, params string[] expected) =>
        Assert.Equal(expected, checklist["items"]!.AsArray().Select(i => (string)i!["status"]!));

    private async Task<string> FootprintAsync(Guid tenantId) =>
        (string)(await TenantProvisioning.ScalarAsSuperuserAsync(fx, """
            SELECT concat_ws('|',
              (SELECT count(*) FROM ti.warehouse WHERE tenant_id = @t), (SELECT count(*) FROM ti.zone WHERE tenant_id = @t),
              (SELECT count(*) FROM ti.bin WHERE tenant_id = @t), (SELECT count(*) FROM ti.unit WHERE tenant_id = @t),
              (SELECT count(*) FROM ti.product WHERE tenant_id = @t), (SELECT count(*) FROM ti.invitation WHERE tenant_id = @t),
              (SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t), (SELECT count(*) FROM ti.outbox_event WHERE tenant_id = @t),
              (SELECT count(*) FROM ti.usage_counter WHERE tenant_id = @t))
            """, ("t", tenantId)))!;
}
