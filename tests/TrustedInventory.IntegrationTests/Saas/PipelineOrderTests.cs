using System.Net;
using Npgsql;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Saas;

/// <summary>
/// SAS-005 central request order, black-box: each request fails two gates at once, and the earlier gate in the order
/// must answer. Order (spec section 22; docs/openapi.yaml): authenticate → membership → access mode (x-access) →
/// permission → entitlement → body → quota reservation in the transaction → domain validation → audit and outbox
/// committed together. Warehouse scope needs the target resource, so services check it after membership.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class PipelineOrderTests(ApiFixture fx)
{
    /// <summary>A body the server must never read when an earlier gate rejects (unknown members are malformed).</summary>
    private const string UnreadableBody = "{\"unexpected\":true}";

    [Fact]
    [Trait("Requirement", "SAS-005")]
    public async Task Authentication_is_decided_before_membership_access_mode_and_body()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "order-auth");
        await BillingRestrictAsync(tenant.TenantId);
        var anonymous = fx.Anonymous();
        anonymous.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.TenantId.ToString());

        await (await anonymous.PostJsonAsync("/api/v1/warehouses", UnreadableBody))
            .ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    [Trait("Requirement", "SAS-005")]
    [Trait("Requirement", "AT-TEN-01")]
    public async Task Membership_is_decided_before_access_mode_so_outsiders_learn_nothing_about_the_tenant()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "order-member");
        await BillingRestrictAsync(tenant.TenantId);

        var problem = await (await fx.Client(fx.Seed.B.Admin, tenant.TenantId).PostJsonAsync("/api/v1/warehouses", UnreadableBody))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Null(problem["recovery"]); // no restriction reasons or recovery action leak to a non-member
    }

    [Fact]
    [Trait("Requirement", "SAS-005")]
    [Trait("Requirement", "LFC-001")]
    public async Task Access_mode_is_decided_before_permission_and_body()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "order-mode");
        var noRoles = await AddMemberWithoutRolesAsync(tenant.TenantId);
        await BillingRestrictAsync(tenant.TenantId);

        var problem = await (await fx.Client(noRoles, tenant.TenantId).PostJsonAsync("/api/v1/warehouses", UnreadableBody))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "SUBSCRIPTION_RESTRICTED");
        Assert.Equal("routine-write", (string?)problem["recovery"]!["blockedCategory"]);
    }

    [Fact]
    [Trait("Requirement", "SAS-005")]
    [Trait("Requirement", "ENT-002")]
    public async Task Permission_is_decided_before_entitlement_and_entitlement_before_the_body()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "order-feature");
        var noRoles = await AddMemberWithoutRolesAsync(tenant.TenantId);
        await MoveToPlanAsync(tenant.TenantId, ["service_api"], limitEach: 100); // no core_inventory

        var denied = await (await fx.Client(noRoles, tenant.TenantId).PostJsonAsync("/api/v1/warehouses", UnreadableBody))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Null(denied["feature"]);

        var notEnabled = await (await fx.Client(tenant.Admin, tenant.TenantId).PostJsonAsync("/api/v1/warehouses", UnreadableBody))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FEATURE_NOT_ENABLED");
        Assert.Equal("core_inventory", (string?)notEnabled["feature"]);
    }

    [Fact]
    [Trait("Requirement", "SAS-005")]
    [Trait("Requirement", "ENT-004")]
    public async Task Quota_is_reserved_before_domain_validation_and_rolled_back_with_it()
    {
        // Duplicate membership is a domain rule: on its own it is INVALID_STATE, and the seat is not kept.
        var open = await TenantProvisioning.ProvisionAsync(fx, "order-quota-open");
        var seatsBefore = await SeatsUsedAsync(open.TenantId);
        var duplicate = await (await fx.Client(open.Admin, open.TenantId).PostJsonAsync("/api/v1/memberships", new { userSubject = open.Admin }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        Assert.Null(duplicate["quota"]);
        Assert.Equal(seatsBefore, await SeatsUsedAsync(open.TenantId));

        // At the seat limit the same request is refused by the quota gate first.
        var full = await TenantProvisioning.ProvisionAsync(fx, "order-quota-full");
        await MoveToPlanAsync(full.TenantId, ["core_inventory"], limitEach: 1); // the administrator holds the only seat
        var limited = await (await fx.Client(full.Admin, full.TenantId).PostJsonAsync("/api/v1/memberships", new { userSubject = full.Admin }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "PLAN_LIMIT_REACHED");
        Assert.Equal("active_seats", (string?)limited["quota"]!["quota"]);
        Assert.Equal(1L, await SeatsUsedAsync(full.TenantId));
    }

    [Fact]
    [Trait("Requirement", "SAS-005")]
    [Trait("Requirement", "ARC-003")]
    public async Task Audit_and_outbox_commit_with_the_change_and_never_with_a_rejected_one()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "order-commit");
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        var (audit0, outbox0) = await AuditAndOutboxAsync(tenant.TenantId);

        await (await admin.PostJsonAsync("/api/v1/warehouses", new { code = "ORD-1", name = "Order", timeZone = "UTC" }))
            .ExpectAsync(HttpStatusCode.Created);
        Assert.Equal((audit0 + 1, outbox0 + 1), await AuditAndOutboxAsync(tenant.TenantId));

        // Domain validation fails inside the transaction: nothing of the command survives.
        await (await admin.PostJsonAsync("/api/v1/warehouses", new { code = "ORD-1", name = "Again", timeZone = "UTC" }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.Equal((audit0 + 1, outbox0 + 1), await AuditAndOutboxAsync(tenant.TenantId));
    }

    private async Task<string> AddMemberWithoutRolesAsync(Guid tenantId)
    {
        var subject = $"user-noroles-{Guid.NewGuid():N}";
        await TenantProvisioning.ExecAsSuperuserAsync(fx,
            "INSERT INTO ti.membership (tenant_id, user_subject, created_by) VALUES (@t, @s, 'seed')", ("t", tenantId), ("s", subject));
        return subject;
    }

    private async Task BillingRestrictAsync(Guid tenantId) => Assert.Equal(1, await TenantProvisioning.ExecAsSuperuserAsync(fx, """
        UPDATE cp.subscription SET state = 'Trialing', price_version_id = NULL, period_start = NULL, period_end = NULL,
               trial_start = now() - interval '15 days', trial_end = now() - interval '1 day'
        WHERE tenant_id = @t
        """, ("t", tenantId)));

    private async Task MoveToPlanAsync(Guid tenantId, string[] features, long limitEach)
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        var plan = await SaasSeed.PlanAsync(conn, $"order-{Guid.NewGuid():N}"[..20], features, limitEach);
        await TenantProvisioning.ExecAsSuperuserAsync(fx, "UPDATE cp.subscription SET plan_version_id = @p WHERE tenant_id = @t",
            ("p", plan), ("t", tenantId));
        await TenantProvisioning.ExecAsSuperuserAsync(fx, """
            UPDATE cp.entitlement_snapshot e SET plan_version_id = p.id, features = p.features, limits = p.quotas
            FROM cp.plan_version p WHERE p.id = @p AND e.tenant_id = @t AND e.is_current
            """, ("p", plan), ("t", tenantId));
    }

    /// <summary>Seat usage as the gate would see it: the stored counter, or active memberships if not yet seeded.</summary>
    private async Task<long> SeatsUsedAsync(Guid tenantId) =>
        (long)(await TenantProvisioning.ScalarAsSuperuserAsync(fx, """
            SELECT coalesce((SELECT used FROM ti.usage_counter WHERE tenant_id = @t AND metric = 'active_seats'),
                            (SELECT count(*) FROM ti.membership WHERE tenant_id = @t AND active))
            """, ("t", tenantId)))!;

    private async Task<(long Audit, long Outbox)> AuditAndOutboxAsync(Guid tenantId) =>
        ((long)(await TenantProvisioning.ScalarAsSuperuserAsync(fx, "SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t", ("t", tenantId)))!,
         (long)(await TenantProvisioning.ScalarAsSuperuserAsync(fx, "SELECT count(*) FROM ti.outbox_event WHERE tenant_id = @t", ("t", tenantId)))!);
}
