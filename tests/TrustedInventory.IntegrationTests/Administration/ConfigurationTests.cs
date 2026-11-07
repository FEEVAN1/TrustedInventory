using System.Net;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Administration;

[Collection(ApiTestGroup.Name)]
public sealed class ConfigurationTests(ApiFixture fx)
{
    private static object Settings(int reservationHours = 12, int overReceipt = 0) => new
    {
        reservationExpiryHours = reservationHours,
        defaultMinRemainingShelfLifeDays = 30,
        overReceiptTolerancePercent = overReceipt,
        approvalExpiryMinutes = 30,
        idempotencyRetentionDays = 7,
        telemetry = new { readingIntervalSeconds = 60, staleAfterSeconds = 300, futureSkewSeconds = 60 },
    };

    [Fact]
    [Trait("Requirement", "CFG-001")]
    [Trait("Requirement", "AUD-001")]
    public async Task Configuration_changes_are_new_versions_with_actor_and_reason()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "config");
        var admin = fx.Client(tenant.Admin, tenant.TenantId);

        var defaults = await admin.GetAsync("/api/v1/configuration");
        var initial = await defaults.ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("\"1\"", defaults.ETag());
        Assert.Equal(24, (int)initial["settings"]!["reservationExpiryHours"]!);

        await (await admin.PostJsonAsync("/api/v1/configuration/versions", new { settings = Settings(), reason = "Shorter holds" }))
            .ExpectProblemAsync((HttpStatusCode)428, "PRECONDITION_REQUIRED");
        var created = await admin.PostJsonAsync("/api/v1/configuration/versions", new { settings = Settings(), reason = "Shorter holds" }, "\"1\"");
        var v2 = await created.ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(2, (int)v2["version"]!);
        Assert.Equal(tenant.Admin, (string?)v2["createdBy"]);
        await (await admin.PostJsonAsync("/api/v1/configuration/versions", new { settings = Settings(6), reason = "stale" }, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.PreconditionFailed, "VERSION_CONFLICT");

        var current = await (await admin.GetAsync("/api/v1/configuration")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(12, (int)current["settings"]!["reservationExpiryHours"]!);
        Assert.Equal("Shorter holds", (string?)current["reason"]);
        var versions = await (await admin.GetAsync("/api/v1/configuration/versions")).ExpectAsync(HttpStatusCode.OK);
        Assert.Single(versions["items"]!.AsArray());

        var audited = await TenantProvisioning.ScalarAsSuperuserAsync(fx,
            "SELECT actor_subject || '|' || reason FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'ConfigurationVersionCreated'",
            ("t", tenant.TenantId));
        Assert.Equal($"{tenant.Admin}|Shorter holds", audited);
    }

    [Fact]
    [Trait("Requirement", "CFG-001")]
    [Trait("Requirement", "VALIDATION")]
    public async Task Invalid_or_incomplete_settings_are_rejected()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "config-invalid");
        var admin = fx.Client(tenant.Admin, tenant.TenantId);

        var overReceipt = await admin.PostJsonAsync("/api/v1/configuration/versions", new { settings = Settings(overReceipt: 5), reason = "x" }, "\"1\"");
        var problem = await overReceipt.ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.Contains(problem["errors"]!.AsArray(), e => (string?)e!["field"] == "settings.overReceiptTolerancePercent");

        await (await admin.PostJsonAsync("/api/v1/configuration/versions", new { settings = new { reservationExpiryHours = 5 }, reason = "x" }, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");

        // Reading configuration needs only catalog.read; changing it needs tenant.config.manage.
        await (await fx.Client(fx.Seed.A.Picker, fx.Seed.A.TenantId).GetAsync("/api/v1/configuration")).ExpectAsync(HttpStatusCode.OK);
        await (await fx.Client(fx.Seed.A.Picker, fx.Seed.A.TenantId).PostJsonAsync("/api/v1/configuration/versions",
                new { settings = Settings(), reason = "x" }, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
    }
}
