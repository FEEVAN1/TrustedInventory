using System.Net;
using Npgsql;
using TrustedInventory.Application.Audit;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Locations;

[Collection(ApiTestGroup.Name)]
public sealed class WarehouseTests(ApiFixture fx)
{
    private static string Code() => "WH-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    [Trait("Requirement", "API-003")]
    [Trait("Requirement", "DATA-07")]
    public async Task Create_then_get_returns_resource_with_etag_and_location()
    {
        var admin = fx.Client(fx.Seed.A.Admin, fx.Seed.A.TenantId);
        var code = Code();

        var created = await admin.PostJsonAsync("/api/v1/warehouses", new { code, name = "Lagos DC", timeZone = "Africa/Lagos" });
        var body = await created.ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("\"1\"", created.ETag());
        Assert.Equal($"/api/v1/warehouses/{body["id"]}", created.Headers.Location?.ToString());
        Assert.Equal(code, (string?)body["code"]);
        Assert.True((bool)body["active"]!);

        var fetched = await admin.GetAsync($"/api/v1/warehouses/{body["id"]}");
        Assert.Equal(code, (string?)(await fetched.ExpectAsync(HttpStatusCode.OK))["code"]);
    }

    [Fact]
    [Trait("Requirement", "DEC-010")]
    public async Task Creating_a_warehouse_creates_its_system_transit_bin()
    {
        var admin = fx.Client(fx.Seed.A.Admin, fx.Seed.A.TenantId);
        var wh = await (await admin.PostJsonAsync("/api/v1/warehouses", new { code = Code(), name = "Transit check", timeZone = "UTC" }))
            .ExpectAsync(HttpStatusCode.Created);

        var bins = await (await admin.GetAsync($"/api/v1/bins?warehouseId={wh["id"]}&kind=Transit")).ExpectAsync(HttpStatusCode.OK);
        var transit = Assert.Single(bins["items"]!.AsArray());
        Assert.Equal("TRANSIT", (string?)transit!["code"]);
    }

    [Fact]
    [Trait("Requirement", "API-002")]
    public async Task Update_requires_if_match_and_rejects_stale_versions()
    {
        var admin = fx.Client(fx.Seed.A.Admin, fx.Seed.A.TenantId);
        var created = await admin.PostJsonAsync("/api/v1/warehouses", new { code = Code(), name = "Versioned", timeZone = "UTC" });
        var id = (await created.ExpectAsync(HttpStatusCode.Created))["id"];

        await (await admin.PatchJsonAsync($"/api/v1/warehouses/{id}", new { name = "No precondition", reason = "test" }, ifMatch: null))
            .ExpectProblemAsync((HttpStatusCode)428, "PRECONDITION_REQUIRED");

        var updated = await admin.PatchJsonAsync($"/api/v1/warehouses/{id}", new { name = "Renamed", reason = "rename" }, "\"1\"");
        Assert.Equal("Renamed", (string?)(await updated.ExpectAsync(HttpStatusCode.OK))["name"]);
        Assert.Equal("\"2\"", updated.ETag());

        await (await admin.PatchJsonAsync($"/api/v1/warehouses/{id}", new { name = "Stale", reason = "stale" }, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.PreconditionFailed, "VERSION_CONFLICT");

        var retired = await admin.PostEmptyAsync($"/api/v1/warehouses/{id}/retire", "\"2\"");
        await retired.ExpectAsync(HttpStatusCode.NoContent);
        Assert.Equal("\"3\"", retired.ETag());
    }

    [Fact]
    [Trait("Requirement", "VALIDATION")]
    [Trait("Requirement", "API-ERRORS")]
    public async Task Invalid_fields_and_unknown_properties_are_rejected()
    {
        var admin = fx.Client(fx.Seed.A.Admin, fx.Seed.A.TenantId);

        var invalid = await admin.PostJsonAsync("/api/v1/warehouses", new { code = "bad code!", name = " padded ", timeZone = "Mars/Olympus" });
        var problem = await invalid.ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        var fields = problem["errors"]!.AsArray().Select(e => (string?)e!["field"]).ToHashSet();
        Assert.Superset(new HashSet<string?> { "code", "name", "timeZone" }, fields);

        // Command bodies never accept tenant or actor fields (API-002).
        await (await admin.PostJsonAsync("/api/v1/warehouses", new { code = Code(), name = "X", timeZone = "UTC", tenantId = fx.Seed.B.TenantId }))
            .ExpectProblemAsync(HttpStatusCode.BadRequest, "MALFORMED_REQUEST");
    }

    [Fact]
    [Trait("Requirement", "IAM-002")]
    [Trait("Requirement", "API-001")]
    public async Task Authentication_and_membership_are_enforced()
    {
        await (await fx.Anonymous().GetAsync("/api/v1/warehouses")).ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");

        var noMfa = fx.Client(fx.Seed.A.Admin, fx.Seed.A.TenantId, mfa: false);
        await (await noMfa.GetAsync("/api/v1/warehouses")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        // Valid token, but the subject has no membership in tenant B.
        var foreign = fx.Client(fx.Seed.A.Admin, fx.Seed.B.TenantId);
        await (await foreign.GetAsync("/api/v1/warehouses")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        var missingTenant = fx.Client(fx.Seed.A.Admin, tenantId: null);
        await (await missingTenant.GetAsync("/api/v1/warehouses")).ExpectProblemAsync(HttpStatusCode.BadRequest, "MALFORMED_REQUEST");

        var wrongKey = fx.Factory.CreateClient();
        wrongKey.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Issue(fx.Seed.A.Admin,
            key: new Microsoft.IdentityModel.Tokens.RsaSecurityKey(System.Security.Cryptography.RSA.Create(2048))));
        await (await wrongKey.GetAsync("/api/v1/warehouses")).ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");

        var wrongAudience = fx.Factory.CreateClient();
        wrongAudience.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Issue(fx.Seed.A.Admin, audience: "another-api"));
        await (await wrongAudience.GetAsync("/api/v1/warehouses")).ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");

        var wrongIssuer = fx.Factory.CreateClient();
        wrongIssuer.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Issue(fx.Seed.A.Admin, issuer: "https://evil.invalid"));
        await (await wrongIssuer.GetAsync("/api/v1/warehouses")).ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");

        // Expired two minutes ago; the API allows only 30 s of clock skew.
        var expired = fx.Factory.CreateClient();
        expired.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Issue(fx.Seed.A.Admin, lifetime: TimeSpan.FromSeconds(-120)));
        await (await expired.GetAsync("/api/v1/warehouses")).ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    [Trait("Requirement", "AT-TEN-01")]
    [Trait("Requirement", "TEN-001")]
    public async Task Tenant_B_cannot_read_or_change_tenant_A_warehouses()
    {
        var adminA = fx.Client(fx.Seed.A.Admin, fx.Seed.A.TenantId);
        var adminB = fx.Client(fx.Seed.B.Admin, fx.Seed.B.TenantId);
        var id = (await (await adminA.PostJsonAsync("/api/v1/warehouses", new { code = Code(), name = "Private", timeZone = "UTC" }))
            .ExpectAsync(HttpStatusCode.Created))["id"];

        await (await adminB.GetAsync($"/api/v1/warehouses/{id}")).ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await adminB.PatchJsonAsync($"/api/v1/warehouses/{id}", new { name = "Hijack", reason = "x" }, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        var list = await (await adminB.GetAsync("/api/v1/warehouses?limit=200")).ExpectAsync(HttpStatusCode.OK);
        Assert.DoesNotContain(list["items"]!.AsArray(), w => (string?)w!["id"] == (string?)id);

        // Referencing A's warehouse from B fails as if it does not exist.
        await (await adminB.PostJsonAsync("/api/v1/zones", new { warehouseId = id, code = "Z1", name = "Zone" }))
            .ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    [Trait("Requirement", "IAM-001")]
    public async Task Permissions_and_warehouse_scope_are_enforced_server_side()
    {
        var picker = fx.Client(fx.Seed.A.Picker, fx.Seed.A.TenantId);
        await (await picker.PostJsonAsync("/api/v1/warehouses", new { code = Code(), name = "Nope", timeZone = "UTC" }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        var noRoles = fx.Client(fx.Seed.A.NoRoles, fx.Seed.A.TenantId);
        await (await noRoles.GetAsync("/api/v1/warehouses")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        // A warehouse-scoped grant cannot create warehouses or edit other warehouses, but can edit its own.
        var scoped = fx.Client(fx.Seed.A.ScopedManager, fx.Seed.A.TenantId);
        await (await scoped.PostJsonAsync("/api/v1/warehouses", new { code = Code(), name = "Nope", timeZone = "UTC" }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        var admin = fx.Client(fx.Seed.A.Admin, fx.Seed.A.TenantId);
        var other = (await (await admin.PostJsonAsync("/api/v1/warehouses", new { code = Code(), name = "Other", timeZone = "UTC" }))
            .ExpectAsync(HttpStatusCode.Created))["id"];
        await (await scoped.PostJsonAsync("/api/v1/zones", new { warehouseId = other, code = "Z1", name = "Zone" }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await scoped.PostJsonAsync("/api/v1/zones", new { warehouseId = fx.Seed.A.ScopedWarehouseId, code = Code(), name = "Own zone" }))
            .ExpectAsync(HttpStatusCode.Created);
    }

    [Fact]
    [Trait("Requirement", "AUD-001")]
    [Trait("Requirement", "AUD-002")]
    public async Task Changes_are_audited_in_an_unbroken_hash_chain()
    {
        var admin = fx.Client(fx.Seed.A.Admin, fx.Seed.A.TenantId);
        var id = (await (await admin.PostJsonAsync("/api/v1/warehouses", new { code = Code(), name = "Audited", timeZone = "UTC" }))
            .ExpectAsync(HttpStatusCode.Created))["id"];
        await (await admin.PatchJsonAsync($"/api/v1/warehouses/{id}", new { name = "Audited 2", reason = "rename for audit" }, "\"1\""))
            .ExpectAsync(HttpStatusCode.OK);

        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT sequence, occurred_at, actor_subject, actor_type::text, request_id, correlation_id, event_type, object_type,
                   object_id, object_version, reason, before_state::text, after_state::text, previous_hash, event_hash
            FROM ti.audit_event WHERE tenant_id = @t ORDER BY sequence
            """, conn);
        cmd.Parameters.AddWithValue("t", fx.Seed.A.TenantId);
        await using var reader = await cmd.ExecuteReaderAsync();

        var previous = AuditHash.Genesis;
        long expected = 1;
        var sawUpdate = false;
        while (await reader.ReadAsync())
        {
            Assert.Equal(expected++, reader.GetInt64(0));
            Assert.Equal(previous, (byte[])reader[13]);
            var record = new AuditRecord(fx.Seed.A.TenantId, reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetString(2), reader.GetString(3), Nullable(reader, 4), Nullable(reader, 5), reader.GetString(6),
                reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetGuid(8), reader.IsDBNull(9) ? null : reader.GetInt32(9),
                Nullable(reader, 10), Json(reader, 11), Json(reader, 12));
            var hash = record.Hash(previous);
            Assert.Equal(hash, (byte[])reader[14]);
            previous = hash;
            sawUpdate |= record.EventType == "WarehouseUpdated" && record.Reason == "rename for audit" && record.ActorSubject == fx.Seed.A.Admin;
        }
        Assert.True(expected > 2);
        Assert.True(sawUpdate);
    }

    private static string? Nullable(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static System.Text.Json.Nodes.JsonNode? Json(NpgsqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : System.Text.Json.Nodes.JsonNode.Parse(r.GetString(i));
}
