using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TrustedInventory.Application.Operations;
using TrustedInventory.Infrastructure;
using TrustedInventory.Infrastructure.Jobs;
using TrustedInventory.Infrastructure.Persistence;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Operations;

/// <summary>Attachments, exports and download links (TEN-005, AUD-004, NFR-EXPORTS, DEC-061) through the API on real PostgreSQL.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class FileTests(ApiFixture fx)
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n");
    private static readonly byte[] SigningKey = Enumerable.Range(1, 32).Select(i => (byte)(i * 3)).ToArray();

    [Fact]
    [Trait("Requirement", "TEN-005")]
    [Trait("Requirement", "ENT-003")]
    public async Task Attachments_are_content_checked_scanned_counted_and_downloadable_only_by_their_caller_through_a_short_link()
    {
        var a = await TenantAsync("files-att-a");
        var b = await TenantAsync("files-att-b");
        await (await Upload(a.Client, Encoding.ASCII.GetBytes("just text"), "notes.txt", "text/plain")).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        await (await Upload(a.Client, Encoding.ASCII.GetBytes("MZ not a document"), "invoice.pdf", "application/octet-stream"))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"); // the content decides, not the name
        await (await Upload(a.Client, Pdf, "photo.png", "image/png")).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"); // content says PDF
        var infected = await (await Upload(a.Client, [.. Pdf, .. Encoding.ASCII.GetBytes("X5O!P%@AP EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*")], "bad.pdf", "application/pdf"))
            .ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("Rejected", (string?)infected["scanStatus"]);
        await (await a.Client.GetAsync($"/api/v1/attachments/{infected["id"]}/download-link")).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");

        var clean = await (await Upload(a.Client, Pdf, "count-sheet.pdf", "application/pdf")).ExpectAsync(HttpStatusCode.Created);
        var id = (string)clean["id"]!;
        Assert.Equal(["Clean", "application/pdf", Convert.ToHexStringLower(SHA256.HashData(Pdf))],
            new[] { (string)clean["scanStatus"]!, (string)clean["mediaType"]!, (string)clean["digest"]! });
        Assert.StartsWith($"{a.Tenant}/attachments/", await ScalarAsync<string>("SELECT object_key FROM ti.attachment WHERE id = @id", ("id", Guid.Parse(id))));
        Assert.True(await ScalarAsync<long>("SELECT used FROM ti.usage_counter WHERE tenant_id = @t AND metric = 'attachment_bytes'", ("t", a.Tenant)) >= Pdf.Length * 2);

        var link = await (await a.Client.GetAsync($"/api/v1/attachments/{id}/download-link")).ExpectAsync(HttpStatusCode.OK);
        Assert.True(DateTimeOffset.Parse((string)link["expiresAt"]!, System.Globalization.CultureInfo.InvariantCulture) <= DateTimeOffset.UtcNow.AddMinutes(15.1));
        var url = (string)link["url"]!;
        var downloaded = await a.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        Assert.Equal(Pdf, await downloaded.Content.ReadAsByteArrayAsync());

        // TEN-005: the same link is useless to another member, to another tenant, after expiry or for another file.
        Assert.Equal(HttpStatusCode.NotFound, (await a.Colleague.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync(url)).StatusCode);
        var links = fx.Factory.Services.GetRequiredService<IDownloadLinks>();
        var expired = links.Issue(a.Tenant, DownloadKind.Attachment, Guid.Parse(id), a.Subject, DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync($"/api/v1/attachments/{id}/download?token={Uri.EscapeDataString(expired)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync(url.Replace(id, (string)infected["id"]!, StringComparison.Ordinal))).StatusCode);
    }

    [Fact]
    [Trait("Requirement", "AUD-004")]
    [Trait("Requirement", "NFR-EXPORTS")]
    [Trait("Requirement", "TEN-005")]
    public async Task Exports_run_asynchronously_carry_filters_range_and_a_signed_manifest_and_download_through_a_reauthorised_link()
    {
        var a = await TenantAsync("files-exp-a");
        var b = await TenantAsync("files-exp-b");
        await SeedMovementsAsync(a, 3);
        await (await a.Client.PostJsonAsync("/api/v1/exports", new { type = "Traceability", filters = new { } }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"); // M5
        await (await a.Colleague.PostJsonAsync("/api/v1/exports", new { type = "Movements", filters = new { } })).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        var queued = await (await a.Client.PostJsonAsync("/api/v1/exports", new { type = "Movements", filters = new { warehouseId = a.Warehouse } }))
            .ExpectAsync(HttpStatusCode.Accepted);
        var audit = await (await a.Client.PostJsonAsync("/api/v1/exports", new { type = "Audit", filters = new { from = DateTimeOffset.UtcNow.AddDays(-1) } }))
            .ExpectAsync(HttpStatusCode.Accepted);
        Assert.Equal("Queued", (string?)queued["status"]);
        await (await a.Client.GetAsync($"/api/v1/exports/{queued["id"]}/download-link")).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");

        await GenerateAsync();
        var done = await (await a.Client.GetAsync($"/api/v1/exports/{queued["id"]}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["Completed", a.Warehouse], new[] { (string)done["status"]!, (string)done["filters"]!["warehouseId"]! });
        Assert.NotNull(done["streamRange"]);
        Assert.StartsWith("hmac-sha256:test-checkpoint-key:", (string)done["manifest"]!["signatureReference"]!, StringComparison.Ordinal);
        Assert.Equal(1, (int)done["manifest"]!["fileCount"]!);
        Assert.InRange(DateTimeOffset.Parse((string)done["retainUntil"]!, System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.UtcNow.AddDays(29), DateTimeOffset.UtcNow.AddDays(31));

        var url = (string)(await (await a.Client.GetAsync($"/api/v1/exports/{queued["id"]}/download-link")).ExpectAsync(HttpStatusCode.OK))["url"]!;
        var response = await a.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal((string)done["manifest"]!["digest"]!, Convert.ToHexStringLower(SHA256.HashData(content))); // integrity manifest (AUD-004)
        var rows = Encoding.UTF8.GetString(content).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        Assert.Equal(4, rows.Count); // one receipt and three bin moves in this warehouse
        Assert.All(rows, r => Assert.NotNull(r["documentType"]));
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync(url)).StatusCode);       // another tenant
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Colleague.GetAsync(url)).StatusCode);   // no exports.create

        var auditDone = await (await a.Client.GetAsync($"/api/v1/exports/{audit["id"]}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Completed", (string?)auditDone["status"]);
        Assert.True((long)auditDone["streamRange"]!["toSequence"]! >= (long)auditDone["streamRange"]!["fromSequence"]!);
        Assert.Equal(2L, await ScalarAsync<long>("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'ExportRequested'", ("t", a.Tenant)));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'ExportDownloaded'", ("t", a.Tenant)));
    }

    // ---------------------------------------------------------------------------------------------------------------

    private sealed record Workspace(Guid Tenant, string Subject, HttpClient Client, HttpClient Colleague, string Warehouse, string A01, string A02, string Product);

    private async Task<Workspace> TenantAsync(string label)
    {
        var tenant = await TenantProvisioning.SignUpAsync(fx, label); // owner: exports.create; audit stream written through the API
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        await TenantProvisioning.GrantSelfAsync(fx, tenant, "WarehouseManager");
        async Task<string> Create(string path, object body) => (string)(await (await admin.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!;
        var warehouse = await Create("/api/v1/warehouses", new { code = "W1", name = "Main", timeZone = "UTC" });
        var zone = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "AMB", name = "Ambient" });
        var a01 = await Create("/api/v1/bins", new { zoneId = zone, code = "A-01", kind = "Storage" });
        var a02 = await Create("/api/v1/bins", new { zoneId = zone, code = "A-02", kind = "Storage" });
        var unit = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        var product = await Create("/api/v1/products", new { sku = "GEN", name = "GEN", handlingProfile = "General", trackingMode = "Quantity", baseUomId = unit, basePrecision = 0 });
        var colleague = $"user-colleague-{Guid.NewGuid():N}";
        var membership = await ScalarAsync<Guid>("INSERT INTO ti.membership (tenant_id, user_subject, created_by) VALUES (@t, @s, 'seed') RETURNING id",
            ("t", tenant.TenantId), ("s", colleague));
        await ExecAsync("""
            INSERT INTO ti.role_grant (tenant_id, membership_id, grantee_subject, role, reason, granted_by, created_by)
            VALUES (@t, @m, @s, 'WarehouseManager', 'test seed', 'seed', 'seed')
            """, ("t", tenant.TenantId), ("m", membership), ("s", colleague));
        return new Workspace(tenant.TenantId, tenant.Admin, admin, fx.Client(colleague, tenant.TenantId), warehouse, a01, a02, product);
    }

    /// <summary>One receipt written as the superuser, then <paramref name="moves"/> real bin transfers through the API.</summary>
    private async Task SeedMovementsAsync(Workspace t, int moves)
    {
        var bucket = await ScalarAsync<Guid>("""
            INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, quality_status, received_at, on_hand_qty, created_by)
            VALUES (@t, @w, @b, @p, 'Available', now(), @q, 'test') RETURNING id
            """, ("t", t.Tenant), ("w", Guid.Parse(t.Warehouse)), ("b", Guid.Parse(t.A01)), ("p", Guid.Parse(t.Product)), ("q", (decimal)moves));
        await ExecAsync("""
            INSERT INTO ti.stock_movement (tenant_id, movement_type, operation_key, product_id, destination_bucket_id, base_qty, document_type, document_id,
                                           actor_subject, occurred_at)
            VALUES (@t, 'Receipt', 'seed:' || gen_random_uuid(), @p, @b, @q, 'Receipt', gen_random_uuid(), 'test', now())
            """, ("t", t.Tenant), ("p", Guid.Parse(t.Product)), ("b", bucket), ("q", (decimal)moves));
        await TenantProvisioning.GrantSelfAsync(fx, new ProvisionedTenant(t.Tenant, t.Subject), "Receiver");
        for (var i = 0; i < moves; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/inventory/bin-transfers")
            {
                Content = JsonContent(new { sourceBucketId = bucket, targetBinId = t.A02, quantity = "1" }),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            await (await t.Client.SendAsync(request)).ExpectAsync(HttpStatusCode.OK);
        }
    }

    private static StringContent JsonContent(object body) =>
        new(System.Text.Json.JsonSerializer.Serialize(body, System.Text.Json.JsonSerializerOptions.Web), Encoding.UTF8, "application/json");

    private static Task<HttpResponseMessage> Upload(HttpClient client, byte[] content, string name, string mediaType)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(file, "file", name);
        return client.PostAsync("/api/v1/attachments", form);
    }

    private async Task GenerateAsync()
    {
        await using var app = DependencyInjection.BuildDataSource(fx.AppConnection);
        await using var platform = DependencyInjection.BuildDataSource(fx.PlatformConnection);
        var runner = new TenantJobRunner(new PlatformDataSource(platform), new AppDataSource(app), new FakeTimeProvider(DateTimeOffset.UtcNow));
        await new ExportGenerationJob(runner, fx.Factory.Services.GetRequiredService<IObjectStore>(), new CheckpointSigningOptions(SigningKey, "test-checkpoint-key"),
            NullLogger<ExportGenerationJob>.Instance).RunOnceAsync(CancellationToken.None);
    }

    private Task<int> ExecAsync(string sql, params (string Name, object Value)[] args) => TenantProvisioning.ExecAsSuperuserAsync(fx, sql, args);

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args) =>
        (T)(await TenantProvisioning.ScalarAsSuperuserAsync(fx, sql, args))!;
}
