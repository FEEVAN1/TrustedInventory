using System.Net;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Audit;

[Collection(ApiTestGroup.Name)]
public sealed class AuditTrailTests(ApiFixture fx)
{
    /// <summary>The chain of a signed-up tenant after <see cref="AuditedTenantAsync"/>, oldest first.</summary>
    private static readonly string[] ExpectedChain =
    [
        "TenantCreated", "MembershipCreated", "RoleGranted", "RoleGranted", "BillingAccountCreated", "TenantReady", "TrialStarted", // signup
        "RoleGranted", // Auditor
        "WarehouseCreated", "WarehouseCreated", "WarehouseCreated",
    ];

    private const int FirstWarehouseSequence = 9;

    /// <summary>A tenant created through real signup, with a few audited changes, its owner also holding Auditor.</summary>
    private async Task<ProvisionedTenant> AuditedTenantAsync(string label)
    {
        var tenant = await TenantProvisioning.SignUpAsync(fx, label);
        await TenantProvisioning.GrantSelfAsync(fx, tenant, "Auditor");
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        for (var i = 0; i < 3; i++)
            await (await admin.PostJsonAsync("/api/v1/warehouses", new { code = $"WH{i}", name = $"Warehouse {i}", timeZone = "UTC" }))
                .ExpectAsync(HttpStatusCode.Created);
        return tenant;
    }

    [Fact]
    [Trait("Requirement", "AUD-001")]
    [Trait("Requirement", "AUD-002")]
    [Trait("Requirement", "API-004")]
    public async Task Events_are_listed_newest_first_with_linked_hashes_and_tamper_evident_cursors()
    {
        var tenant = await AuditedTenantAsync("list");
        var auditor = fx.Client(tenant.Admin, tenant.TenantId);

        var all = (await (await auditor.GetAsync("/api/v1/audit/events?limit=200")).ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray();
        // Provisioning writes into the tenant's own chain from genesis (AUD-001).
        Assert.Equal(ExpectedChain.Reverse(), all.Select(e => (string)e!["eventType"]!));
        for (var i = 0; i < all.Count - 1; i++)
        {
            Assert.Equal((long)all[i]!["sequence"]! - 1, (long)all[i + 1]!["sequence"]!);
            Assert.Equal((string?)all[i + 1]!["eventHash"], (string?)all[i]!["previousHash"]);
        }
        Assert.Equal(new string('0', 64), (string?)all[^1]!["previousHash"]);
        Assert.Equal("TenantCreated", (string?)all[^1]!["eventType"]);

        var first = await (await auditor.GetAsync("/api/v1/audit/events?limit=3")).ExpectAsync(HttpStatusCode.OK);
        var cursor = (string)first["nextCursor"]!;
        var second = await (await auditor.GetAsync($"/api/v1/audit/events?limit=3&cursor={cursor}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal((long)all[3]!["sequence"]!, (long)second["items"]![0]!["sequence"]!);

        await (await auditor.GetAsync($"/api/v1/audit/events?limit=3&objectType=Warehouse&cursor={cursor}"))
            .ExpectProblemAsync(HttpStatusCode.BadRequest, "INVALID_CURSOR");
    }

    [Fact]
    [Trait("Requirement", "AT-AUD-01")]
    [Trait("Requirement", "AUD-002")]
    public async Task An_intact_chain_verifies()
    {
        var tenant = await AuditedTenantAsync("verify-ok");
        var result = await (await fx.Client(tenant.Admin, tenant.TenantId).PostJsonAsync("/api/v1/audit/verify", new { }))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.True((bool)result["verified"]!);
        Assert.Equal(ExpectedChain.Length, (long)result["eventsChecked"]!);
        Assert.Null(result["failure"]);
    }

    [Fact]
    [Trait("Requirement", "AT-AUD-01")]
    [Trait("Requirement", "OPS-001")]
    public async Task An_altered_event_fails_verification_and_opens_one_high_alert()
    {
        var tenant = await AuditedTenantAsync("tamper");
        // Rename the second warehouse in its creation event.
        const int forged = FirstWarehouseSequence + 1;
        await TamperAsync($"UPDATE ti.audit_event SET after_state = jsonb_set(after_state, '{{name}}', '\"Forged\"') WHERE tenant_id = @t AND sequence = {forged}",
            tenant.TenantId);

        var client = fx.Client(tenant.Admin, tenant.TenantId);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await (await client.PostJsonAsync("/api/v1/audit/verify", new { })).ExpectAsync(HttpStatusCode.OK);
            Assert.False((bool)result["verified"]!);
            Assert.Equal("HashMismatch", (string?)result["failure"]);
            Assert.Equal(forged, (long)result["firstFailureSequence"]!);
        }

        var alerts = await TenantProvisioning.ScalarAsSuperuserAsync(fx,
            "SELECT count(*) FROM ti.alert WHERE tenant_id = @t AND type = 'AuditChainFailure' AND severity = 'High' AND status = 'Open'",
            ("t", tenant.TenantId));
        Assert.Equal(1L, alerts);

        // Verifying only the untouched prefix still succeeds.
        var prefix = await (await client.PostJsonAsync("/api/v1/audit/verify", new { fromSequence = 1, toSequence = forged - 1 })).ExpectAsync(HttpStatusCode.OK);
        Assert.True((bool)prefix["verified"]!);
    }

    [Fact]
    [Trait("Requirement", "AT-AUD-01")]
    public async Task A_deleted_event_is_reported_as_a_sequence_gap()
    {
        var tenant = await AuditedTenantAsync("gap");
        await TamperAsync("DELETE FROM ti.audit_event WHERE tenant_id = @t AND sequence = 6", tenant.TenantId);

        var result = await (await fx.Client(tenant.Admin, tenant.TenantId).PostJsonAsync("/api/v1/audit/verify", new { }))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.False((bool)result["verified"]!);
        Assert.Equal("SequenceGap", (string?)result["failure"]);
        Assert.Equal(6, (long)result["firstFailureSequence"]!);
    }

    [Fact]
    [Trait("Requirement", "AT-TEN-01")]
    [Trait("Requirement", "IAM-001")]
    public async Task Audit_events_are_tenant_isolated_and_need_audit_read()
    {
        var tenant = await AuditedTenantAsync("isolated");
        var other = fx.Client(fx.Seed.B.Auditor, fx.Seed.B.TenantId);
        var hashes = (await (await fx.Client(tenant.Admin, tenant.TenantId).GetAsync("/api/v1/audit/events?limit=200"))
            .ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray().Select(e => (string?)e!["eventHash"]).ToHashSet();

        var seenByB = (await (await other.GetAsync("/api/v1/audit/events?limit=200")).ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray();
        Assert.DoesNotContain(seenByB, e => hashes.Contains((string?)e!["eventHash"]));

        await (await fx.Client(fx.Seed.A.Picker, fx.Seed.A.TenantId).GetAsync("/api/v1/audit/events"))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await fx.Client(fx.Seed.A.Picker, fx.Seed.A.TenantId).PostJsonAsync("/api/v1/audit/verify", new { }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    /// <summary>Simulates an attacker with database superuser access bypassing the append-only trigger.</summary>
    private async Task TamperAsync(string sql, Guid tenantId)
    {
        await TenantProvisioning.ExecAsSuperuserAsync(fx, "ALTER TABLE ti.audit_event DISABLE TRIGGER audit_event_append_only");
        try
        {
            Assert.Equal(1, await TenantProvisioning.ExecAsSuperuserAsync(fx, sql, ("t", tenantId)));
        }
        finally
        {
            await TenantProvisioning.ExecAsSuperuserAsync(fx, "ALTER TABLE ti.audit_event ENABLE TRIGGER audit_event_append_only");
        }
    }
}
