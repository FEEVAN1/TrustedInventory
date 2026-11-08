using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Saas;

/// <summary>
/// Tenant boundaries of the shared SaaS deployment (section 22): home region at provisioning (SAS-002), slugs as
/// navigation only (SAS-003), and the control-plane decision function scoped to the caller's tenant (SAS-004).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed partial class TenantBoundaryTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "SAS-002")]
    public async Task Signup_records_the_deployments_home_region_on_the_tenant()
    {
        const string region = "eu-test-1";
        await using var factory = fx.Factory.WithWebHostBuilder(web => web.UseSetting("Saas:HomeRegion", region));
        var owner = $"user-region-{Guid.NewGuid():N}";
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue(owner));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saas/signup")
        {
            Content = JsonContent.Create(new
            {
                businessName = "Region Traders",
                requestedSlug = $"r-{Guid.NewGuid():N}"[..20],
                billingCountry = "NG",
                planVersionId = fx.Seed.UnlimitedPlanId,
                termsVersion = "terms-2026-11",
                privacyVersion = "privacy-2026-11",
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var accepted = await (await client.SendAsync(request)).ExpectAsync(HttpStatusCode.Accepted);
        var tenantId = Guid.Parse((string)accepted["tenantId"]!);

        Assert.Equal(region, await ScalarAsync<string>(fx.SuperuserConnection, "SELECT home_region FROM ti.tenant WHERE id = @t", ("t", tenantId)));
        var view = await (await fx.Client(SeedData.PlatformAdmin, tenantId: null).GetAsync($"/platform/v1/tenants/{tenantId}"))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(region, (string?)view["tenant"]!["homeRegion"]);
    }

    [Fact]
    [Trait("Requirement", "SAS-003")]
    public async Task A_slug_never_selects_a_tenant_whoever_sends_it()
    {
        var slug = await ScalarAsync<string>(fx.SuperuserConnection, "SELECT slug FROM ti.tenant WHERE id = @t", ("t", fx.Seed.A.TenantId));

        // Neither a member nor an outsider can address the workspace by slug, in any position.
        foreach (var subject in new[] { fx.Seed.A.Admin, fx.Seed.B.Admin })
        {
            var client = fx.Client(subject, tenantId: null);
            foreach (var request in new[]
            {
                Get("/api/v1/warehouses", ("X-Tenant-Id", slug)),
                Get("/api/v1/warehouses", ("X-Tenant-Slug", slug)),
                Get($"/api/v1/warehouses?tenant={slug}"),
                Get($"/api/v1/warehouses?tenantSlug={slug}"),
            })
            {
                await (await client.SendAsync(request)).ExpectProblemAsync(HttpStatusCode.BadRequest, "MALFORMED_REQUEST");
            }
        }

        // The contract offers no way to pass one: no path segment or parameter is a slug. Slugs appear only as
        // response fields, the signup request body and the closure confirmation text.
        var contract = await File.ReadAllTextAsync(ContractPath());
        Assert.Empty(SlugPath().Matches(contract));
        Assert.Empty(SlugParameter().Matches(contract));
    }

    [Fact]
    [Trait("Requirement", "SAS-004")]
    [Trait("Requirement", "AT-TEN-01")]
    public async Task Tenant_access_function_answers_only_for_the_callers_own_tenant()
    {
        await using var conn = new NpgsqlConnection(fx.AppConnection);
        await conn.OpenAsync();

        // No tenant context: fail closed.
        Assert.Empty(await AccessRowsAsync(conn, null));
        // A tenant context sees exactly that tenant; consecutive transactions on one session do not mix.
        Assert.Equal([fx.Seed.A.TenantId], await AccessRowsAsync(conn, fx.Seed.A.TenantId));
        Assert.Equal([fx.Seed.B.TenantId], await AccessRowsAsync(conn, fx.Seed.B.TenantId));
        Assert.Empty(await AccessRowsAsync(conn, Guid.NewGuid()));

        // It cannot be steered: there is no overload taking a tenant id.
        await using (var steer = new NpgsqlCommand("SELECT * FROM cp.tenant_access(@t)", conn))
        {
            steer.Parameters.AddWithValue("t", fx.Seed.B.TenantId);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => steer.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.UndefinedFunction, ex.SqlState);
        }

        // Direct reads of tenant-facing billing rows (for billing.read screens) are forced-RLS scoped the same way:
        // nothing without context, only the caller's own rows with it, never another tenant's.
        foreach (var table in new[] { "cp.subscription", "cp.entitlement_snapshot", "cp.billing_account" })
        {
            Assert.Equal(0L, await DirectCountAsync(conn, table, null, "true"));
            Assert.True(await DirectCountAsync(conn, table, fx.Seed.A.TenantId, "true") > 0, $"{table}: own rows visible");
            Assert.Equal(0L, await DirectCountAsync(conn, table, fx.Seed.A.TenantId, $"tenant_id <> '{fx.Seed.A.TenantId}'"));
        }
        // Tax references stay restricted even for the tenant's own account (section 27).
        await using (var tax = new NpgsqlCommand("SELECT tax_reference FROM cp.billing_account", conn))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => tax.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        // Only the runtime role may call it; control-plane, platform and membership roles may not.
        var callers = await ListAsync(fx.SuperuserConnection, """
            SELECT r FROM unnest(ARRAY['ti_app','ti_control','ti_platform','ti_membership_reader']) r
            WHERE has_function_privilege(r, 'cp.tenant_access()', 'EXECUTE')
            """);
        Assert.Equal(["ti_app"], callers);
    }

    private static async Task<List<Guid>> AccessRowsAsync(NpgsqlConnection conn, Guid? tenantId)
    {
        await using var tx = await conn.BeginTransactionAsync();
        if (tenantId is { } t)
        {
            await using var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", conn, tx);
            set.Parameters.AddWithValue("t", t.ToString());
            await set.ExecuteNonQueryAsync();
        }
        var rows = new List<Guid>();
        await using (var cmd = new NpgsqlCommand("SELECT tenant_id FROM cp.tenant_access()", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                rows.Add(reader.GetGuid(0));
        await tx.RollbackAsync();
        return rows;
    }

    private static async Task<long> DirectCountAsync(NpgsqlConnection conn, string table, Guid? tenantId, string where)
    {
        await using var tx = await conn.BeginTransactionAsync();
        if (tenantId is { } t)
        {
            await using var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", conn, tx);
            set.Parameters.AddWithValue("t", t.ToString());
            await set.ExecuteNonQueryAsync();
        }
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE {where}", conn, tx);
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        await tx.RollbackAsync();
        return count;
    }

    private static HttpRequestMessage Get(string url, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        return request;
    }

    private static async Task<T> ScalarAsync<T>(string connection, string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> ListAsync(string connection, string sql)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }

    private static string ContractPath()
    {
        var dir = new DirectoryInfo(Environment.GetEnvironmentVariable("TI_REPO_ROOT") is { Length: > 0 } root ? root : AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "openapi.yaml")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "docs", "openapi.yaml");
    }

    /// <summary>A path key (two-space indent under paths) containing "slug".</summary>
    [GeneratedRegex(@"^  /[^\n]*slug[^\n]*:\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SlugPath();

    /// <summary>A parameter or header whose name contains "slug".</summary>
    [GeneratedRegex(@"^\s*-?\s*name:\s*[""']?[\w-]*slug[\w-]*[""']?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SlugParameter();
}
