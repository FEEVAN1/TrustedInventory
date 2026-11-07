using System.Net;
using Npgsql;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Organization;

/// <summary>A tenant provisioned through the platform API, for tests that must not disturb the shared seed tenants.</summary>
public sealed record ProvisionedTenant(Guid TenantId, string Admin);

public static class TenantProvisioning
{
    public static async Task<ProvisionedTenant> ProvisionAsync(ApiFixture fx, string label)
    {
        var admin = $"user-{label}-{Guid.NewGuid():N}";
        var platform = fx.Client(SeedData.PlatformAdmin, tenantId: null);
        var body = await (await platform.PostJsonAsync("/api/v1/platform/tenants",
                new { name = $"Tenant {label} {Guid.NewGuid():N}"[..40], initialAdministratorSubject = admin }))
            .ExpectAsync(HttpStatusCode.Created);
        return new ProvisionedTenant(Guid.Parse((string)body["id"]!), admin);
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
