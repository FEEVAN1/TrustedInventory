using System.Net;
using System.Net.Http.Json;
using Npgsql;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Organization;

/// <summary>A dedicated Ready tenant with an active subscription, for tests that must not disturb the shared seed tenants.</summary>
public sealed record ProvisionedTenant(Guid TenantId, string Admin);

public static class TenantProvisioning
{
    public const string TestPlan = "test-unlimited";

    /// <summary>
    /// Arranges a Ready tenant as the superuser: tenant row, audit stream, one TenantAdministrator member and an
    /// Active subscription on the unlimited test plan. Platform tenant creation no longer exists (DEC-037); signup
    /// is exercised by its own tests.
    /// </summary>
    public static async Task<ProvisionedTenant> ProvisionAsync(ApiFixture fx, string label)
    {
        var admin = $"user-{label}-{Guid.NewGuid():N}";
        var tenantId = Guid.CreateVersion7();
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        await Exec(conn, """
            INSERT INTO ti.tenant (id, name, slug, lifecycle, home_region, ready_at, created_by)
            VALUES (@id, @name, @slug, 'Ready', 'default', now(), 'seed')
            """, ("id", tenantId), ("name", $"Tenant {label} {Guid.NewGuid():N}"[..40]), ("slug", "t-" + tenantId.ToString("N")[..20]));
        await Exec(conn, "INSERT INTO ti.audit_stream (tenant_id) VALUES (@id)", ("id", tenantId));
        var membershipId = Guid.CreateVersion7();
        await Exec(conn, "INSERT INTO ti.membership (id, tenant_id, user_subject, created_by) VALUES (@id, @t, @s, 'seed')",
            ("id", membershipId), ("t", tenantId), ("s", admin));
        await Exec(conn, """
            INSERT INTO ti.role_grant (tenant_id, membership_id, grantee_subject, role, reason, granted_by, created_by)
            VALUES (@t, @m, @s, 'TenantAdministrator', 'test seed', 'seed', 'seed')
            """, ("t", tenantId), ("m", membershipId), ("s", admin));
        await SaasSeed.ActiveSubscriptionAsync(conn, tenantId, await PlanIdAsync(conn, TestPlan));
        return new ProvisionedTenant(tenantId, admin);
    }

    /// <summary>
    /// Creates a tenant through the real signup API on the unlimited test plan, so provisioning writes its own audit
    /// events (TenantCreated first). The signer holds TenantOwner and TenantAdministrator (DEC-036). Use this when a test
    /// depends on the tenant's audit chain; <see cref="ProvisionAsync"/> arranges rows directly and writes no events.
    /// </summary>
    public static async Task<ProvisionedTenant> SignUpAsync(ApiFixture fx, string label)
    {
        var owner = $"user-{label}-{Guid.NewGuid():N}";
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saas/signup")
        {
            Content = JsonContent.Create(new
            {
                businessName = $"Tenant {label}",
                requestedSlug = $"a-{Guid.NewGuid():N}"[..20],
                billingCountry = "NG",
                planVersionId = fx.Seed.UnlimitedPlanId,
                termsVersion = "terms-2026-11",
                privacyVersion = "privacy-2026-11",
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var body = await (await fx.Client(owner, tenantId: null).SendAsync(request)).ExpectAsync(HttpStatusCode.Accepted);
        Assert.Equal("Ready", (string?)body["status"]);
        return new ProvisionedTenant(Guid.Parse((string)body["tenantId"]!), owner);
    }

    public static async Task<Guid> PlanIdAsync(NpgsqlConnection conn, string code)
    {
        await using var cmd = new NpgsqlCommand("SELECT id FROM cp.plan_version WHERE plan_code = @c ORDER BY version DESC LIMIT 1", conn);
        cmd.Parameters.AddWithValue("c", code);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Grants a role to the tenant administrator (granted by a second administrator, as SOD-3 needs for QA).</summary>
    public static async Task GrantSelfAsync(ApiFixture fx, ProvisionedTenant tenant, string role)
    {
        var client = fx.Client(tenant.Admin, tenant.TenantId);
        var memberships = await (await client.GetAsync("/api/v1/memberships")).ExpectAsync(HttpStatusCode.OK);
        var mine = memberships["items"]!.AsArray().Single(m => (string?)m!["userSubject"] == tenant.Admin)!;
        await (await client.PostJsonAsync($"/api/v1/memberships/{mine["id"]}/role-grants", new { role, reason = "test setup" }))
            .ExpectAsync(HttpStatusCode.Created);
    }

    public static async Task<int> ExecAsSuperuserAsync(ApiFixture fx, string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsSuperuserAsync(ApiFixture fx, string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteScalarAsync();
    }
}
