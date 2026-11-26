using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TrustedInventory.Application.Operations;
using TrustedInventory.Infrastructure;
using TrustedInventory.Infrastructure.Billing;
using TrustedInventory.Infrastructure.Jobs;
using TrustedInventory.Infrastructure.Persistence;
using TrustedInventory.IntegrationTests.Billing;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Saas;

/// <summary>Closure, platform lifecycle controls and the tenant archive export (LFC-002, LFC-003, AT-LFC-01, AT-LFC-02, DEC-070, DEC-071).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class OffboardingTests(ApiFixture fx)
{
    private static readonly byte[] CheckpointKey = Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray();

    private SimulatedBillingProvider Simulator => fx.Factory.Services.GetRequiredService<SimulatedBillingProvider>();

    [Fact]
    [Trait("Requirement", "LFC-002")]
    [Trait("Requirement", "SOD-7")]
    public async Task Closure_needs_the_owner_a_fresh_reauthentication_and_the_exact_confirmation()
    {
        var t = await SignedUpAsync("close-guard");
        var slug = await SlugAsync(t.TenantId);

        // A tenant administrator who is not the owner cannot close (ONB-006).
        var admin = await TenantProvisioning.ProvisionAsync(fx, "close-admin");
        await (await Close(fx.Client(admin.Admin, admin.TenantId), admin.Admin, $"CLOSE {await SlugAsync(admin.TenantId)}"))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        var wrong = await (await Close(t.Owner, t.Subject, $"close {slug}")).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.Contains("confirmation", wrong.ToJsonString(), StringComparison.Ordinal);
        await (await Close(t.Owner, t.Subject, $"CLOSE {slug}", reauth: TestTokens.Issue(t.Subject, authTime: DateTimeOffset.UtcNow.AddMinutes(-6))))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "REAUTH_REQUIRED");
        await (await Close(t.Owner, t.Subject, $"CLOSE {slug}", reauth: TestTokens.Issue(t.Subject, mfa: false)))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "REAUTH_REQUIRED");

        Assert.Equal("Ready", await ScalarAsync<string>("SELECT lifecycle::text FROM ti.tenant WHERE id = @t", ("t", t.TenantId)));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM cp.deletion_record WHERE tenant_id = @t", ("t", t.TenantId)));
        await (await t.Owner.GetAsync("/api/v1/saas/closure")).ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    [Trait("Requirement", "AT-LFC-01")]
    [Trait("Requirement", "LFC-002")]
    public async Task Controlled_stock_blocks_closure_with_exact_blockers_and_changes_nothing()
    {
        var t = await SignedUpAsync("close-blocked");
        var slug = await SlugAsync(t.TenantId);
        await MedicalStockAsync(t, quality: "Hold");
        var key = Guid.NewGuid();

        var problem = await (await Close(t.Owner, t.Subject, $"CLOSE {slug}", key: key)).ExpectProblemAsync(HttpStatusCode.Conflict, "RETENTION_BLOCKED");
        var codes = problem["blockers"]!.AsArray().Select(b => (string)b!["code"]!).Order().ToArray();
        Assert.Equal(["ACTIVE_HOLDS", "MEDICAL_STOCK_ON_HAND"], codes);

        // Nothing changed: the workspace is Ready, the subscription untouched, no export queued; the record says Blocked.
        Assert.Equal("Ready", await ScalarAsync<string>("SELECT lifecycle::text FROM ti.tenant WHERE id = @t", ("t", t.TenantId)));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM ti.export_job WHERE tenant_id = @t", ("t", t.TenantId)));
        var status = await (await t.Owner.GetAsync("/api/v1/saas/closure")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Blocked", (string?)status["status"]);
        Assert.Equal(2, status["blockers"]!.AsArray().Count);

        // The same key replays the refusal; safety work and normal reads continue.
        await (await Close(t.Owner, t.Subject, $"CLOSE {slug}", key: key)).ExpectProblemAsync(HttpStatusCode.Conflict, "RETENTION_BLOCKED");
        await (await t.Owner.GetAsync("/api/v1/warehouses")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM cp.deletion_record WHERE tenant_id = @t", ("t", t.TenantId)));
        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'TenantClosureBlocked'", ("t", t.TenantId)));
    }

    [Fact]
    [Trait("Requirement", "AT-LFC-01")]
    [Trait("Requirement", "LFC-002")]
    [Trait("Requirement", "LFC-001")]
    public async Task Closing_a_paid_workspace_stops_renewal_archives_it_and_opens_the_export_window()
    {
        var t = await SignedUpAsync("close-paid");
        await PayAsync(t);
        var slug = await SlugAsync(t.TenantId);
        var key = Guid.NewGuid();

        var closure = await (await Close(t.Owner, t.Subject, $"CLOSE {slug}", key: key, deletion: true)).ExpectAsync(HttpStatusCode.Accepted);
        Assert.Equal("ExportWindow", (string?)closure["status"]);
        Assert.True((bool)closure["requestedDeletion"]!);
        Assert.Equal(0, (int)closure["outstandingInvoiceCount"]!);
        Assert.NotNull(closure["archiveExportId"]);

        // Nothing renews: the subscription is cancelled at period end, and the window runs 90 days past the paid period.
        Assert.True(await ScalarAsync<bool>("SELECT cancel_at_period_end FROM cp.subscription WHERE tenant_id = @t", ("t", t.TenantId)));
        var periodEnd = await ScalarAsync<DateTime>("SELECT period_end FROM cp.subscription WHERE tenant_id = @t", ("t", t.TenantId));
        Assert.Equal(DateTime.SpecifyKind(periodEnd, DateTimeKind.Utc).AddDays(90),
            DateTimeOffset.Parse((string)closure["exportWindowEndsAt"]!, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime);
        Assert.Equal("Archived", await ScalarAsync<string>("SELECT lifecycle::text FROM ti.tenant WHERE id = @t", ("t", t.TenantId)));
        Assert.Equal("TenantArchive", await ScalarAsync<string>("SELECT type::text FROM ti.export_job WHERE id = @id",
            ("id", Guid.Parse((string)closure["archiveExportId"]!))));

        // Routine work and reads stop at once (no worker involved); the owner keeps closure status and exports; billing stays open.
        var write = await (await t.Owner.PostJsonAsync("/api/v1/warehouses", new { code = "W9", name = "Late", timeZone = "UTC" }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "SUBSCRIPTION_RESTRICTED");
        Assert.Equal("view-closure", (string?)write["recovery"]!["action"]);
        await (await t.Owner.GetAsync("/api/v1/warehouses")).ExpectProblemAsync(HttpStatusCode.Forbidden, "SUBSCRIPTION_RESTRICTED");
        await (await t.Owner.GetAsync("/api/v1/saas/closure")).ExpectAsync(HttpStatusCode.OK);
        await (await t.Owner.GetAsync($"/api/v1/exports/{closure["archiveExportId"]}")).ExpectAsync(HttpStatusCode.OK);
        await (await t.Owner.GetAsync("/api/v1/saas/invoices")).ExpectAsync(HttpStatusCode.OK);

        // Once archived, the closure route is refused by the access gate (recovery view-closure), even for the same key; the status
        // route answers a client whose response was lost. No second closure or export is created.
        var retry = await (await Close(t.Owner, t.Subject, $"CLOSE {slug}", key: key, deletion: true))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "SUBSCRIPTION_RESTRICTED");
        Assert.Equal("view-closure", (string?)retry["recovery"]!["action"]);
        var status = await (await t.Owner.GetAsync("/api/v1/saas/closure")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal((string?)closure["id"], (string?)status["id"]);
        Assert.Equal((string?)closure["archiveExportId"], (string?)status["archiveExportId"]);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM ti.export_job WHERE tenant_id = @t", ("t", t.TenantId)));
        foreach (var evt in new[] { "SubscriptionCancellationRequested", "TenantClosureRequested", "TenantArchived" })
            Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = @e",
                ("t", t.TenantId), ("e", evt)));
    }

    [Fact]
    [Trait("Requirement", "LFC-002")]
    [Trait("Requirement", "LFC-004")]
    [Trait("Requirement", "AUD-001")]
    public async Task Platform_operations_set_a_retention_hold_and_withdraw_a_closure_with_reasons_in_the_tenant_chain()
    {
        var t = await SignedUpAsync("close-withdraw");
        var slug = await SlugAsync(t.TenantId);
        await (await Close(t.Owner, t.Subject, $"CLOSE {slug}")).ExpectAsync(HttpStatusCode.Accepted);
        var platform = fx.Client(SeedData.PlatformAdmin, tenantId: null);
        var stranger = fx.Client(t.Subject, tenantId: null);

        var tenant = await platform.GetAsync($"/platform/v1/tenants/{t.TenantId}");
        var version = tenant.ETag();
        await (await stranger.PostJsonAsync($"/platform/v1/tenants/{t.TenantId}/retention-hold",
            new { hold = true, reason = "Litigation hold", reference = "LH-2026-001" }, version)).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await platform.PostJsonAsync($"/platform/v1/tenants/{t.TenantId}/retention-hold",
            new { hold = true, reason = "Litigation hold", reference = (string?)null }, version)).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        var held = await platform.PostJsonAsync($"/platform/v1/tenants/{t.TenantId}/retention-hold",
            new { hold = true, reason = "Litigation hold", reference = "LH-2026-001" }, version);
        var heldBody = await held.ExpectAsync(HttpStatusCode.OK);
        Assert.True((bool)heldBody["tenant"]!["retentionHold"]!);
        await (await platform.PostJsonAsync($"/platform/v1/tenants/{t.TenantId}/retention-hold",
            new { hold = false, reason = "Released", reference = (string?)null }, version)).ExpectProblemAsync(HttpStatusCode.PreconditionFailed, "VERSION_CONFLICT");

        var withdrawn = await (await platform.PostJsonAsync($"/platform/v1/tenants/{t.TenantId}/closure/withdraw",
            new { reason = "Owner asked to keep the workspace" }, held.ETag())).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Ready", (string?)withdrawn["tenant"]!["lifecycle"]);
        Assert.Equal("Normal", (string?)withdrawn["tenant"]!["accessMode"]);
        Assert.True((bool)withdrawn["tenant"]!["retentionHold"]!); // the hold outlives the closure
        Assert.Equal("Withdrawn", (string?)(await (await t.Owner.GetAsync("/api/v1/saas/closure")).ExpectAsync(HttpStatusCode.OK))["status"]);
        await (await t.Owner.GetAsync("/api/v1/warehouses")).ExpectAsync(HttpStatusCode.OK);
        foreach (var evt in new[] { "RetentionHoldSet", "TenantClosureWithdrawn", "TenantRestoredFromArchive" })
            Assert.Equal(1L, await ScalarAsync<long>(
                "SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = @e AND actor_subject = @a",
                ("t", t.TenantId), ("e", evt), ("a", SeedData.PlatformAdmin)));
    }

    [Fact]
    [Trait("Requirement", "LFC-003")]
    [Trait("Requirement", "AT-LFC-02")]
    [Trait("Requirement", "AT-TEN-01")]
    public async Task The_tenant_archive_holds_only_the_owners_tenant_with_lineage_audit_integrity_and_a_verifiable_manifest()
    {
        var a = await SignedUpAsync("archive-a");
        var b = await SignedUpAsync("archive-b");
        var (lotA, productA) = await MedicalStockAsync(a, quality: "Available");
        var (lotB, _) = await MedicalStockAsync(b, quality: "Available");

        // Owner only: a tenant administrator who is not the owner may not create it (LFC-003).
        var admin = await TenantProvisioning.ProvisionAsync(fx, "archive-admin");
        await (await fx.Client(admin.Admin, admin.TenantId).PostJsonAsync("/api/v1/exports", new { type = "TenantArchive", filters = new { } }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        var job = await (await a.Owner.PostJsonAsync("/api/v1/exports", new { type = "TenantArchive", filters = new { } })).ExpectAsync(HttpStatusCode.Accepted);
        var exportId = (string)job["id"]!;
        await GenerateAsync();
        var done = await (await a.Owner.GetAsync($"/api/v1/exports/{exportId}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Completed", (string?)done["status"]);

        var url = (string)(await (await a.Owner.GetAsync($"/api/v1/exports/{exportId}/download-link")).ExpectAsync(HttpStatusCode.OK))["url"]!;
        var response = await a.Owner.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal((string?)done["manifest"]!["digest"], Convert.ToHexStringLower(SHA256.HashData(bytes)));

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var files = zip.Entries.ToDictionary(e => e.FullName, e => Read(e));
        var manifest = JsonNode.Parse(files["manifest.json"])!;
        Assert.Equal(a.TenantId.ToString(), (string?)manifest["tenantId"]);
        foreach (var entry in manifest["files"]!.AsArray())
        {
            var path = (string)entry!["path"]!;
            Assert.Equal((string?)entry["sha256"], Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(files[path]))));
        }
        Assert.Equal(manifest["files"]!.AsArray().Count + 1, zip.Entries.Count);
        Assert.Equal(TenantArchive.Tables.Count, files.Keys.Count(k => k.StartsWith("data/", StringComparison.Ordinal)));

        // Only tenant A: every row carries A's id and nothing of tenant B appears anywhere (TEN-003).
        foreach (var (path, text) in files.Where(f => f.Key.StartsWith("data/", StringComparison.Ordinal)))
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Assert.Equal(a.TenantId.ToString(), (string?)JsonNode.Parse(line)!["tenant_id"]);
        Assert.DoesNotContain(files, f => f.Value.Contains(b.TenantId.ToString(), StringComparison.Ordinal)
            || f.Value.Contains(lotB.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => f.Value.Contains("reauth_reference", StringComparison.Ordinal)
            || f.Value.Contains("token_hash", StringComparison.Ordinal));

        // Lineage and catalog are complete; the audit chain can be checked against the integrity manifest offline.
        Assert.Contains(lotA.ToString(), files["lineage/lots.jsonl"], StringComparison.Ordinal);
        Assert.Contains(productA.ToString(), files["data/product.jsonl"], StringComparison.Ordinal);
        Assert.Contains(lotA.ToString(), files["data/stock_bucket.jsonl"], StringComparison.Ordinal);
        var events = files["audit/events.jsonl"].Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(await ScalarAsync<long>("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND sequence <= @s",
            ("t", a.TenantId), ("s", (long)manifest["auditStreamTo"]!)), events.Count);
        for (var i = 1; i < events.Count; i++)
            Assert.Equal((string?)events[i - 1]["eventHash"], (string?)events[i]["previousHash"]);
        var integrity = JsonNode.Parse(files["audit/integrity.json"])!;
        Assert.Equal((string?)events[^1]["eventHash"], (string?)integrity["lastHash"]);

        // The link serves only its caller and tenant, and only for 15 minutes.
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync(url)).StatusCode);
        var expired = fx.Factory.Services.GetRequiredService<IDownloadLinks>()
            .Issue(a.TenantId, DownloadKind.Export, Guid.Parse(exportId), a.Subject, DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.GetAsync($"/api/v1/exports/{exportId}/download?token={Uri.EscapeDataString(expired)}")).StatusCode);
    }

    [Fact]
    [Trait("Requirement", "LFC-003")]
    public async Task Every_tenant_table_is_either_archived_or_excluded_for_a_stated_reason()
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT c.table_name FROM information_schema.columns c JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name AND t.table_type = 'BASE TABLE'
            WHERE c.table_schema = 'ti' AND c.column_name IN ('tenant_id') ORDER BY 1
            """, conn);
        var tables = new List<string>();
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                tables.Add(r.GetString(0));
        tables.Add("tenant");
        var classified = TenantArchive.Tables.Concat(TenantArchive.Excluded.Keys).ToHashSet(StringComparer.Ordinal);
        Assert.All(tables, t => Assert.Contains(t, classified));
        Assert.Empty(TenantArchive.Tables.Intersect(TenantArchive.Excluded.Keys));
    }

    // ---- helpers ----

    internal sealed record Owned(Guid TenantId, string Subject, HttpClient Owner);

    private async Task<Owned> SignedUpAsync(string label)
    {
        var tenant = await TenantProvisioning.SignUpAsync(fx, label);
        return new Owned(tenant.TenantId, tenant.Admin, fx.Client(tenant.Admin, tenant.TenantId));
    }

    internal static Task<HttpResponseMessage> Close(HttpClient owner, string subject, string confirmation, Guid? key = null, bool deletion = false,
        string? reauth = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saas/closure")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new
            {
                reason = "We are moving to another system",
                requestedDeletion = deletion,
                confirmation,
                reauthToken = reauth ?? TestTokens.Issue(subject),
            }),
        };
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        return owner.SendAsync(request);
    }

    private async Task PayAsync(Owned t)
    {
        var price = await CheckoutTests.PublishedPriceAsync(fx, 2500, features: ["core_inventory", "medical_storage"], limitEach: 1000);
        var digest = (string)(await (await t.Owner.PostJsonAsync("/api/v1/saas/checkout/preview", new { priceVersionId = price })).ExpectAsync(HttpStatusCode.OK))["previewDigest"]!;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saas/checkout")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { priceVersionId = price, acceptedPreviewDigest = digest }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var session = await (await t.Owner.SendAsync(request)).ExpectAsync(HttpStatusCode.Created);
        var e = Simulator.PayCheckout(Guid.Parse((string)session["checkoutId"]!));
        var webhook = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/billing/simulator") { Content = new ByteArrayContent(e.Body) };
        webhook.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        webhook.Headers.TryAddWithoutValidation(SimulatedBillingProvider.SignatureHeader, e.Signature);
        await (await fx.Anonymous().SendAsync(webhook)).ExpectAsync(HttpStatusCode.OK);
        await fx.Factory.Services.GetRequiredService<BillingEventJob>().RunOnceAsync(CancellationToken.None);
        Assert.Equal("Active", await ScalarAsync<string>("SELECT state::text FROM cp.subscription WHERE tenant_id = @t", ("t", t.TenantId)));
    }

    /// <summary>A medical lot with five units in a bucket of the given quality, through the API where routes exist.</summary>
    internal async Task<(Guid Lot, Guid Product)> MedicalStockAsync(Owned t, string quality)
    {
        async Task<Guid> Create(string path, object body) =>
            Guid.Parse((string)(await (await t.Owner.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!);
        var warehouse = await Create("/api/v1/warehouses", new { code = "MW1", name = "Medical store", timeZone = "UTC" });
        var zone = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "MZ", name = "Ambient" });
        var bin = await Create("/api/v1/bins", new { zoneId = zone, code = "MB1", kind = "Storage" });
        var unit = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        var profile = await Create("/api/v1/storage-profiles", new
        {
            name = "Ambient medical", minC = 15, maxC = 25, monitoringRequired = false,
            excursionPolicy = new { immediateHold = true, notes = "Synthetic policy" }, reason = "CFG-002 needs a profile (DEC-100)",
        });
        var product = await Create("/api/v1/products",
            new { sku = "MED-1", name = "Synthetic medical item", handlingProfile = "Medical", trackingMode = "Lot", baseUomId = unit, basePrecision = 0, storageProfileId = profile });
        var lot = await ScalarAsync<Guid>("""
            INSERT INTO ti.lot (tenant_id, product_id, handling_profile, lot_number, expiry_date, created_by)
            VALUES (@t, @p, 'Medical', 'LOT-SYN-1', current_date + 365, 'test') RETURNING id
            """, ("t", t.TenantId), ("p", product));
        await TenantProvisioning.ExecAsSuperuserAsync(fx, """
            INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, lot_id, quality_status, received_at, on_hand_qty, created_by)
            VALUES (@t, @w, @b, @p, @l, @q::ti.quality_status, now(), 5, 'test')
            """, ("t", t.TenantId), ("w", warehouse), ("b", bin), ("p", product), ("l", lot), ("q", quality));
        return (lot, product);
    }

    internal async Task GenerateAsync()
    {
        await using var app = DependencyInjection.BuildDataSource(fx.AppConnection);
        await using var platform = DependencyInjection.BuildDataSource(fx.PlatformConnection);
        var runner = new TenantJobRunner(new PlatformDataSource(platform), new AppDataSource(app), new FakeTimeProvider(DateTimeOffset.UtcNow));
        await new ExportGenerationJob(runner, fx.Factory.Services.GetRequiredService<IObjectStore>(), new CheckpointSigningOptions(CheckpointKey, "test-checkpoint-key"),
            NullLogger<ExportGenerationJob>.Instance).RunOnceAsync(CancellationToken.None);
    }

    private static string Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private Task<string> SlugAsync(Guid tenantId) => ScalarAsync<string>("SELECT slug FROM ti.tenant WHERE id = @t", ("t", tenantId));

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args) =>
        (T)(await TenantProvisioning.ScalarAsSuperuserAsync(fx, sql, args))!;
}
