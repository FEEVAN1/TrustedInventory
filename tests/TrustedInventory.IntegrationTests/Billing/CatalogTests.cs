using System.Net;
using System.Text.Json.Nodes;
using Npgsql;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Billing;

/// <summary>Platform plan catalog (ENT-001, ENT-002, BIL-001, DEC-062) through /platform/v1 on real PostgreSQL.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class CatalogTests(ApiFixture fx)
{
    private static object Limits(long each) => new
    {
        active_seats = each, active_warehouses = each, active_skus = each, active_devices = 0L, attachment_bytes = each * 1000,
    };

    private static string Code() => "cat-" + Guid.NewGuid().ToString("N")[..12];

    private HttpClient Billing() => fx.Client(SeedData.PlatformBilling, tenantId: null);

    private static object Plan(string code) => new
    {
        planCode = code, name = "Catalog test plan", features = new[] { "core_inventory", "service_api" }, limits = Limits(5), trialDays = 14,
        reason = "catalog test",
    };

    private static object Price(string currency = "XTS", long amount = 2500) => new
    {
        amount = new { currency, minorUnits = amount, exponent = 2 }, interval = "Month", activeFrom = DateTimeOffset.UtcNow.AddMinutes(-1),
        reason = "catalog test",
    };

    [Fact]
    [Trait("Requirement", "ENT-001")]
    [Trait("Requirement", "ENT-002")]
    [Trait("Requirement", "BIL-001")]
    public async Task A_draft_gets_prices_then_publishes_and_is_frozen_with_every_change_recorded()
    {
        var client = Billing();
        var code = Code();
        var created = await client.PostJsonAsync("/platform/v1/plan-versions", Plan(code));
        var draft = await created.ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("Draft", (string?)draft["status"]);
        Assert.Equal(1, (int)draft["version"]!);
        Assert.Equal("\"1\"", created.ETag());
        var id = (string)draft["id"]!;

        // Not public while a draft; the second version of the same code is numbered 2.
        Assert.DoesNotContain(await PublicPlanIdsAsync(), x => x == id);
        var second = await (await client.PostJsonAsync("/platform/v1/plan-versions", Plan(code))).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(2, (int)second["version"]!);

        // Publishing needs a price; a price needs the current revision.
        await (await client.PostJsonAsync($"/platform/v1/plan-versions/{id}/publish", new { reason = "go" }, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        var priced = await client.PostJsonAsync($"/platform/v1/plan-versions/{id}/price-versions", Price(), "\"1\"");
        var price = await priced.ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("\"2\"", priced.ETag());
        Assert.Equal(2500L, (long)price["amount"]!["minorUnits"]!);
        Assert.Equal("XTS", (string?)price["amount"]!["currency"]);
        Assert.False((bool)price["displayApproved"]!);
        await (await client.PostJsonAsync($"/platform/v1/plan-versions/{id}/price-versions", Price(), "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.PreconditionFailed, "VERSION_CONFLICT");

        var published = await (await client.PostJsonAsync($"/platform/v1/plan-versions/{id}/publish", new { reason = "go" }, "\"2\""))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Published", (string?)published["status"]);
        Assert.Contains(await PublicPlanIdsAsync(), x => x == id);

        // Frozen: no new price, no second publish (ENT-001).
        await (await client.PostJsonAsync($"/platform/v1/plan-versions/{id}/price-versions", Price(), "\"3\""))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        await (await client.PostJsonAsync($"/platform/v1/plan-versions/{id}/publish", new { reason = "again" }, "\"3\""))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");

        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        await using var events = new NpgsqlCommand("""
            SELECT string_agg(action::text, ',' ORDER BY occurred_at, action) FROM cp.catalog_event WHERE plan_version_id = @id
            """, conn);
        events.Parameters.AddWithValue("id", Guid.Parse(id));
        Assert.Equal("PlanVersionCreated,PriceVersionCreated,PlanVersionPublished", (string?)await events.ExecuteScalarAsync());
        await using var actor = new NpgsqlCommand("SELECT DISTINCT actor_subject FROM cp.catalog_event WHERE plan_version_id = @id", conn);
        actor.Parameters.AddWithValue("id", Guid.Parse(id));
        Assert.Equal(SeedData.PlatformBilling, (string?)await actor.ExecuteScalarAsync());
    }

    [Fact]
    [Trait("Requirement", "ENT-001")]
    public async Task Catalog_evidence_is_append_only_and_a_published_plan_rejects_direct_edits()
    {
        var client = Billing();
        var draft = await (await client.PostJsonAsync("/platform/v1/plan-versions", Plan(Code()))).ExpectAsync(HttpStatusCode.Created);
        var id = Guid.Parse((string)draft["id"]!);
        await (await client.PostJsonAsync($"/platform/v1/plan-versions/{id}/price-versions", Price(), "\"1\"")).ExpectAsync(HttpStatusCode.Created);
        await (await client.PostJsonAsync($"/platform/v1/plan-versions/{id}/publish", new { reason = "go" }, "\"2\"")).ExpectAsync(HttpStatusCode.OK);

        await using var conn = new NpgsqlConnection(fx.ControlConnection);
        await conn.OpenAsync();
        foreach (var sql in new[]
                 {
                     "UPDATE cp.catalog_event SET reason = 'rewritten' WHERE plan_version_id = @id",
                     "UPDATE cp.plan_version SET display_name = 'renamed' WHERE id = @id",
                     "UPDATE cp.plan_version SET revision = revision + 1 WHERE id = @id",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", id);
            await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        }
        await using var delete = new NpgsqlCommand("DELETE FROM cp.catalog_event WHERE plan_version_id = @id", conn);
        delete.Parameters.AddWithValue("id", id);
        await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
    }

    [Theory]
    [Trait("Requirement", "BIL-001")]
    [InlineData("USD", 2500L)]
    [InlineData("NGN", 2500L)]
    [InlineData("XTS", 0L)]
    public async Task Only_a_positive_test_currency_price_is_accepted_while_the_simulator_is_the_provider(string currency, long amount)
    {
        var client = Billing();
        var draft = await (await client.PostJsonAsync("/platform/v1/plan-versions", Plan(Code()))).ExpectAsync(HttpStatusCode.Created);
        await (await client.PostJsonAsync($"/platform/v1/plan-versions/{draft["id"]}/price-versions", Price(currency, amount), "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
    }

    [Fact]
    [Trait("Requirement", "ENT-002")]
    public async Task Plans_must_use_catalog_feature_and_quota_keys_with_every_limit_set()
    {
        var client = Billing();
        var bad = new object[]
        {
            new { planCode = Code(), name = "x", features = new[] { "teleportation" }, limits = Limits(1), trialDays = 14, reason = "r" },
            new { planCode = Code(), name = "x", features = new[] { "core_inventory" }, limits = new { active_seats = 1 }, trialDays = 14, reason = "r" },
            new { planCode = Code(), name = "x", features = new[] { "core_inventory" }, limits = Limits(-1), trialDays = 14, reason = "r" },
            new { planCode = "Bad_Code", name = "x", features = new[] { "core_inventory" }, limits = Limits(1), trialDays = 14, reason = "r" },
            new { planCode = Code(), name = "x", features = new[] { "core_inventory" }, limits = Limits(1), trialDays = 91, reason = "r" },
            new { planCode = Code(), name = "x", features = new[] { "core_inventory" }, limits = Limits(1), trialDays = 14, reason = " " },
        };
        foreach (var body in bad)
        {
            var response = await client.PostJsonAsync("/platform/v1/plan-versions", body);
            Assert.True(response.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest,
                $"{System.Text.Json.JsonSerializer.Serialize(body)} → {(int)response.StatusCode}");
        }
    }

    [Fact]
    [Trait("Requirement", "SAS-004")]
    [Trait("Requirement", "ENT-001")]
    public async Task Platform_operations_can_view_but_not_change_the_catalog_and_tenant_users_reach_neither()
    {
        var operations = fx.Client(SeedData.PlatformAdmin, tenantId: null);
        var list = await (await operations.GetAsync("/platform/v1/plan-versions?status=Draft")).ExpectAsync(HttpStatusCode.OK);
        Assert.All(list["items"]!.AsArray(), p => Assert.Equal("Draft", (string?)p!["status"]));
        await (await operations.PostJsonAsync("/platform/v1/plan-versions", Plan(Code()))).ExpectAsync(HttpStatusCode.Forbidden);

        var tenantAdmin = fx.Client(fx.Seed.A.Admin, tenantId: null);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.GetAsync("/platform/v1/plan-versions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.PostJsonAsync("/platform/v1/plan-versions", Plan(Code()))).StatusCode);
        // Platform routes need MFA like the rest of the console (DEC-026).
        var noMfa = fx.Client(SeedData.PlatformBilling, tenantId: null, mfa: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await noMfa.PostJsonAsync("/platform/v1/plan-versions", Plan(Code()))).StatusCode);
    }

    [Fact]
    [Trait("Requirement", "ENT-001")]
    public async Task Listing_filters_by_plan_code_and_pages_with_a_cursor_bound_to_the_filter()
    {
        var client = Billing();
        var code = Code();
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
            ids.Add((string)(await (await client.PostJsonAsync("/platform/v1/plan-versions", Plan(code))).ExpectAsync(HttpStatusCode.Created))["id"]!);

        var first = await (await client.GetAsync($"/platform/v1/plan-versions?planCode={code}&limit=2")).ExpectAsync(HttpStatusCode.OK);
        var cursor = (string)first["nextCursor"]!;
        var second = await (await client.GetAsync($"/platform/v1/plan-versions?planCode={code}&limit=2&cursor={Uri.EscapeDataString(cursor)}"))
            .ExpectAsync(HttpStatusCode.OK);
        var seen = first["items"]!.AsArray().Concat(second["items"]!.AsArray()).Select(p => (string)p!["id"]!).ToList();
        Assert.Equal(ids.AsEnumerable().Reverse(), seen);
        Assert.Null((string?)second["nextCursor"]);
        Assert.Equal([3, 2, 1], first["items"]!.AsArray().Concat(second["items"]!.AsArray()).Select(p => (int)p!["version"]!));

        await (await client.GetAsync($"/platform/v1/plan-versions?planCode={Code()}&cursor={Uri.EscapeDataString(cursor)}"))
            .ExpectProblemAsync(HttpStatusCode.BadRequest, "INVALID_CURSOR");
    }

    private async Task<List<string>> PublicPlanIdsAsync()
    {
        var ids = new List<string>();
        string? cursor = null;
        do
        {
            var page = await (await fx.Anonymous().GetAsync("/api/v1/saas/plans?limit=200" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))))
                .ExpectAsync(HttpStatusCode.OK);
            ids.AddRange(page["items"]!.AsArray().Select(p => (string)p!["id"]!));
            cursor = (string?)page["nextCursor"];
        }
        while (cursor is not null);
        return ids;
    }
}
