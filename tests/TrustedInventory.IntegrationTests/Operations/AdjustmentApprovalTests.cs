using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Operations;

/// <summary>Approvals and adjustments (EXC-004, IAM-003, IAM-004, SOD-1, SOD-2, SOD-4, DEC-057) through the API on real PostgreSQL.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class AdjustmentApprovalTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "AT-EXC-02")]
    [Trait("Requirement", "IAM-003")]
    [Trait("Requirement", "EXC-004")]
    [Trait("Requirement", "STK-007")]
    public async Task A_changed_adjustment_cannot_post_with_the_earlier_approval_and_the_current_one_posts_once()
    {
        var w = await WorldAsync("adj-stale");
        var bucket = await BucketAsync(w, w.General, 10);
        var adjustment = await RequestAsync(w, new { bucketId = bucket, binId = w.A01, productId = w.General, delta = "-3", reasonCode = "DAMAGE", narrative = "Crushed pallet" });
        var first = await ApproveAsync(w, adjustment, version: 1);
        var signed = await (await w.Requester.GetAsync($"/api/v1/adjustments/{adjustment}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["Approved", first], new[] { (string)signed["status"]!, (string)signed["approvalId"]! });

        // AT-EXC-02: change the quantity; the earlier approval is void and cannot post the changed request.
        var changed = await (await w.Requester.PatchJsonAsync($"/api/v1/adjustments/{adjustment}", new { delta = "-4" }, "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["Requested", "2"], new[] { (string)changed["status"]!, changed["version"]!.ToString() });
        Assert.NotEqual((string)signed["contentDigest"]!, (string)changed["contentDigest"]!);
        Assert.Equal("Invalidated", (string?)(await (await w.Requester.GetAsync($"/api/v1/approvals/{first}")).ExpectAsync(HttpStatusCode.OK))["status"]);
        await (await PostAsync(w.Requester, $"/api/v1/adjustments/{adjustment}/post", Guid.NewGuid(), "\"2\"", new { approvalId = first }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "APPROVAL_STALE");
        Assert.Equal("10|0", await QtyAsync(bucket));

        var second = await ApproveAsync(w, adjustment, version: 2);

        // The binding covers content, not just the version: evidence slipped in behind the approval's back voids it too.
        var slipped = await AttachmentAsync(w);
        await ExecAsync("UPDATE ti.attachment SET entity_type = 'Adjustment', entity_id = @a WHERE id = @id",
            ("a", Guid.Parse(adjustment)), ("id", Guid.Parse(slipped)));
        await (await PostAsync(w.Requester, $"/api/v1/adjustments/{adjustment}/post", Guid.NewGuid(), "\"2\"", new { approvalId = second }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "APPROVAL_STALE");
        await ExecAsync("UPDATE ti.attachment SET entity_type = NULL, entity_id = NULL WHERE id = @id", ("id", Guid.Parse(slipped)));

        var key = Guid.NewGuid();
        var posted = await (await PostAsync(w.Requester, $"/api/v1/adjustments/{adjustment}/post", key, "\"2\"", new { approvalId = second }))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Posted", (string?)posted["status"]);
        Assert.Equal("6|0", await QtyAsync(bucket));
        var replay = await (await PostAsync(w.Requester, $"/api/v1/adjustments/{adjustment}/post", key, "\"2\"", new { approvalId = second }))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.True(JsonNode.DeepEquals(posted, replay));
        await (await PostAsync(w.Requester, $"/api/v1/adjustments/{adjustment}/post", Guid.NewGuid(), $"\"{posted["version"]}\"", new { approvalId = second }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        Assert.Equal("6|0", await QtyAsync(bucket));
        Assert.Equal("Consumed", (string?)(await (await w.Requester.GetAsync($"/api/v1/approvals/{second}")).ExpectAsync(HttpStatusCode.OK))["status"]);
        Assert.Equal("AdjustmentDecrease|2|4", await ScalarAsync<string>("""
            SELECT m.movement_type::text || '|' || (SELECT count(*) FROM ti.approval WHERE tenant_id = @t)::text || '|' || trim_scale(m.base_qty)::text
            FROM ti.stock_movement m WHERE m.tenant_id = @t AND m.document_type = 'Adjustment'
            """, ("t", w.Tenant)));
    }

    [Fact]
    [Trait("Requirement", "AT-IAM-01")]
    [Trait("Requirement", "SOD-1")]
    [Trait("Requirement", "SOD-4")]
    [Trait("Requirement", "IAM-004")]
    public async Task Self_approval_stale_reauthentication_an_expired_approval_and_a_revoked_signer_cannot_post()
    {
        var w = await WorldAsync("adj-iam");
        var bucket = await BucketAsync(w, w.General, 10);
        var adjustment = await RequestAsync(w, new { bucketId = bucket, binId = w.A01, productId = w.General, delta = "-1", reasonCode = "LOSS", narrative = "Missing unit" });

        // SOD-1: the requester also holds signing authority but cannot sign their own request.
        var own = await RequestApprovalAsync(w.Requester, adjustment, 1);
        await (await Sign(w.Requester, own, TestTokens.Issue(w.RequesterSubject))).ExpectProblemAsync(HttpStatusCode.Conflict, "APPROVAL_SELF");

        // IAM-004: a stale or single-factor re-authentication is refused before anything changes.
        await (await Sign(w.Approver, own, TestTokens.Issue(w.ApproverSubject, authTime: DateTimeOffset.UtcNow.AddMinutes(-6))))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "REAUTH_REQUIRED");
        await (await Sign(w.Approver, own, TestTokens.Issue(w.ApproverSubject, mfa: false))).ExpectProblemAsync(HttpStatusCode.Forbidden, "REAUTH_REQUIRED");
        await (await Sign(w.Approver, own, TestTokens.Issue(w.RequesterSubject))).ExpectProblemAsync(HttpStatusCode.Forbidden, "REAUTH_REQUIRED"); // someone else's token
        Assert.Equal("Pending", (string?)(await (await w.Approver.GetAsync($"/api/v1/approvals/{own}")).ExpectAsync(HttpStatusCode.OK))["status"]);

        // Expired: a signature lasts 30 minutes.
        var signed = await (await Sign(w.Approver, own, TestTokens.Issue(w.ApproverSubject))).ExpectAsync(HttpStatusCode.OK);
        Assert.Null(signed["reauthToken"]);
        Assert.NotNull((string?)signed["reauthVerifiedAt"]);
        await ExecAsync("""
            SET session_replication_role = replica;
            UPDATE ti.approval SET signed_at = signed_at - interval '31 minutes', expires_at = expires_at - interval '31 minutes' WHERE id = @a;
            """, ("a", Guid.Parse(own)));
        Assert.Equal("Expired", (string?)(await (await w.Approver.GetAsync($"/api/v1/approvals/{own}")).ExpectAsync(HttpStatusCode.OK))["status"]);
        await (await PostAsync(w.Requester, $"/api/v1/adjustments/{adjustment}/post", Guid.NewGuid(), "\"1\"", new { approvalId = own }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "APPROVAL_STALE");

        // SOD-4: a signer whose membership is revoked after signing no longer authorises posting.
        var fresh = await RequestApprovalAsync(w.Requester, adjustment, 1);
        await (await Sign(w.Approver, fresh, TestTokens.Issue(w.ApproverSubject))).ExpectAsync(HttpStatusCode.OK);
        await (await w.Requester.PostJsonAsync($"/api/v1/memberships/{w.ApproverMembership}/deactivate", new { reason = "left the company" }, "\"1\""))
            .ExpectAsync(HttpStatusCode.OK);
        await (await PostAsync(w.Requester, $"/api/v1/adjustments/{adjustment}/post", Guid.NewGuid(), "\"1\"", new { approvalId = fresh }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "APPROVAL_STALE");
        Assert.Equal("10|0", await QtyAsync(bucket));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.stock_movement WHERE tenant_id = @t AND document_type = 'Adjustment'", w.Tenant));
    }

    [Fact]
    [Trait("Requirement", "SOD-2")]
    [Trait("Requirement", "EXC-004")]
    public async Task Medical_adjustments_need_quality_signing_authority_and_increases_land_on_hold()
    {
        var w = await WorldAsync("adj-medical");
        var lot = await LotAsync(w, w.Medical, "B-1");
        var adjustment = await RequestAsync(w, new { binId = w.C01, productId = w.Medical, lotId = lot, delta = "5", reasonCode = "FOUND", narrative = "Found in cold room" });
        var body = await (await w.Requester.GetAsync($"/api/v1/adjustments/{adjustment}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Hold", (string?)body["qualityStatus"]);    // DEC-022
        Assert.Null(body["bucketId"]);

        var approval = await RequestApprovalAsync(w.Requester, adjustment, 1);
        await (await Sign(w.Approver, approval, TestTokens.Issue(w.ApproverSubject))).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await Sign(w.Qa, approval, TestTokens.Issue(w.QaSubject))).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await Sign(w.Both, approval, TestTokens.Issue(w.BothSubject))).ExpectAsync(HttpStatusCode.OK);
        await (await PostAsync(w.Requester, $"/api/v1/adjustments/{adjustment}/post", Guid.NewGuid(), "\"1\"", new { approvalId = approval }))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("5|0|Hold", await ScalarAsync<string>("""
            SELECT trim_scale(on_hand_qty)::text || '|' || trim_scale(reserved_qty)::text || '|' || quality_status::text
            FROM ti.stock_bucket WHERE bin_id = @b AND product_id = @p
            """, ("b", Guid.Parse(w.C01)), ("p", Guid.Parse(w.Medical))));
    }

    [Fact]
    [Trait("Requirement", "EXC-004")]
    [Trait("Requirement", "IAM-003")]
    public async Task Reductions_never_breach_reserved_stock_and_evidence_cancel_and_rejection_follow_the_rules()
    {
        var w = await WorldAsync("adj-rules");
        var bucket = await BucketAsync(w, w.General, 10, reserved: 8);
        await (await w.Requester.PostJsonAsync("/api/v1/adjustments", new { bucketId = bucket, binId = w.A01, productId = w.General, delta = "-3", reasonCode = "DAMAGE", narrative = "x" }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INSUFFICIENT_STOCK");
        await (await w.Requester.PostJsonAsync("/api/v1/adjustments", new { binId = w.Transit, productId = w.General, qualityStatus = "Available", delta = "1", reasonCode = "FOUND", narrative = "x" }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        await (await w.Requester.PostJsonAsync("/api/v1/adjustments", new { binId = w.A01, productId = w.General, qualityStatus = "Available", delta = "0", reasonCode = "FOUND", narrative = "x" }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");

        // A reduction allowed at request is rechecked at posting: reserved rose in between.
        var reduce = await RequestAsync(w, new { bucketId = bucket, binId = w.A01, productId = w.General, delta = "-2", reasonCode = "DAMAGE", narrative = "Torn bags" });
        var approval = await ApproveAsync(w, reduce, version: 1);
        await ExecAsync("UPDATE ti.stock_bucket SET reserved_qty = 9 WHERE id = @b", ("b", Guid.Parse(bucket)));
        await (await PostAsync(w.Requester, $"/api/v1/adjustments/{reduce}/post", Guid.NewGuid(), "\"1\"", new { approvalId = approval }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INSUFFICIENT_STOCK");
        Assert.Equal("10|9", await QtyAsync(bucket));

        // Evidence files bind to one record only, and are part of what the approval signs.
        var evidence = await AttachmentAsync(w);
        var withEvidence = await RequestAsync(w, new
        {
            binId = w.A01, productId = w.General, qualityStatus = "Available", delta = "4", reasonCode = "FOUND", narrative = "Recount found more",
            evidenceAttachmentIds = new[] { evidence },
        });
        await (await w.Requester.PostJsonAsync("/api/v1/adjustments", new
        {
            binId = w.A01, productId = w.General, qualityStatus = "Available", delta = "1", reasonCode = "FOUND", narrative = "x", evidenceAttachmentIds = new[] { evidence },
        })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        var pending = await RequestApprovalAsync(w.Requester, withEvidence, 1);
        await (await w.Requester.PostJsonAsync($"/api/v1/adjustments/{withEvidence}/cancel", new { reason = "Duplicate request" }, "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Invalidated", (string?)(await (await w.Requester.GetAsync($"/api/v1/approvals/{pending}")).ExpectAsync(HttpStatusCode.OK))["status"]);

        // A rejection closes the request.
        var rejectMe = await RequestAsync(w, new { binId = w.A01, productId = w.General, qualityStatus = "Available", delta = "2", reasonCode = "FOUND", narrative = "x" });
        var toReject = await RequestApprovalAsync(w.Requester, rejectMe, 1);
        Assert.Contains(await ItemsAsync(w.Approver, "/api/v1/approvals?awaitingMySignature=true"), a => (string?)a["id"] == toReject);
        Assert.DoesNotContain(await ItemsAsync(w.Requester, "/api/v1/approvals?awaitingMySignature=true"), a => (string?)a["id"] == toReject);
        var rejected = await (await w.Approver.PostJsonAsync($"/api/v1/approvals/{toReject}/reject", new { reason = "No evidence" }, "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["Rejected", "Rejection"], new[] { (string)rejected["status"]!, (string)rejected["meaning"]! });
        Assert.Equal("Rejected", (string?)(await (await w.Requester.GetAsync($"/api/v1/adjustments/{rejectMe}")).ExpectAsync(HttpStatusCode.OK))["status"]);
    }

    // ---------------------------------------------------------------------------------------------------------------

    private sealed record World(Guid Tenant, HttpClient Requester, string RequesterSubject, HttpClient Approver, string ApproverSubject,
        string ApproverMembership, HttpClient Qa, string QaSubject, HttpClient Both, string BothSubject, string Warehouse, string A01, string C01,
        string Transit, string General, string Medical);

    private async Task<World> WorldAsync(string label)
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, label);
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        foreach (var role in new[] { "WarehouseManager", "InventoryApprover" })
            await TenantProvisioning.GrantSelfAsync(fx, tenant, role);
        async Task<string> Create(string path, object body) => (string)(await (await admin.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!;
        var warehouse = await Create("/api/v1/warehouses", new { code = "W1", name = "Main", timeZone = "UTC" });
        var ambient = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "AMB", name = "Ambient" });
        var cold = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "COLD", name = "Cold", tempMinC = 2, tempMaxC = 8 });
        var a01 = await Create("/api/v1/bins", new { zoneId = ambient, code = "A-01", kind = "Storage" });
        var c01 = await Create("/api/v1/bins", new { zoneId = cold, code = "C-01", kind = "Storage" });
        var transit = (await ScalarAsync<Guid>("SELECT id FROM ti.bin WHERE warehouse_id = @w AND kind = 'Transit'", ("w", Guid.Parse(warehouse)))).ToString();
        var unit = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        var profile = await Create("/api/v1/storage-profiles", new
        {
            name = "Cold chain", minC = 2, maxC = 8, monitoringRequired = true, excursionPolicy = new { immediateHold = true, notes = "QA reviewed" }, reason = "test",
        });
        var general = await Create("/api/v1/products", new { sku = "GEN", name = "GEN", handlingProfile = "General", trackingMode = "Quantity", baseUomId = unit, basePrecision = 0 });
        var medical = await Create("/api/v1/products", new
        {
            sku = "MED", name = "MED", handlingProfile = "Medical", trackingMode = "Lot", baseUomId = unit, basePrecision = 0, storageProfileId = profile,
        });
        var (approver, approverMembership) = await MemberAsync(tenant.TenantId, "approver", "InventoryApprover");
        var (qa, _) = await MemberAsync(tenant.TenantId, "qa", "QaOfficer");
        var (both, _) = await MemberAsync(tenant.TenantId, "both", "InventoryApprover", "QaOfficer");
        return new World(tenant.TenantId, admin, tenant.Admin, fx.Client(approver, tenant.TenantId), approver, approverMembership.ToString(),
            fx.Client(qa, tenant.TenantId), qa, fx.Client(both, tenant.TenantId), both, warehouse, a01, c01, transit, general, medical);
    }

    private async Task<(string Subject, Guid Membership)> MemberAsync(Guid tenant, string label, params string[] roles)
    {
        var subject = $"user-{label}-{Guid.NewGuid():N}";
        var membership = await ScalarAsync<Guid>("INSERT INTO ti.membership (tenant_id, user_subject, created_by) VALUES (@t, @s, 'seed') RETURNING id",
            ("t", tenant), ("s", subject));
        foreach (var role in roles)
            await ExecAsync("""
                INSERT INTO ti.role_grant (tenant_id, membership_id, grantee_subject, role, reason, granted_by, created_by)
                VALUES (@t, @m, @s, @r::ti.role_name, 'test seed', 'seed', 'seed')
                """, ("t", tenant), ("m", membership), ("s", subject), ("r", role));
        return (subject, membership);
    }

    private static async Task<string> RequestAsync(World w, object body) =>
        (string)(await (await w.Requester.PostJsonAsync("/api/v1/adjustments", body)).ExpectAsync(HttpStatusCode.Created))["id"]!;

    private static async Task<string> RequestApprovalAsync(HttpClient client, string adjustment, int version) =>
        (string)(await (await client.PostJsonAsync("/api/v1/approvals", new
        {
            subjectType = "Adjustment", subjectId = adjustment, subjectVersion = version, action = "Post", reason = "Please approve",
        })).ExpectAsync(HttpStatusCode.Created))["id"]!;

    private static Task<HttpResponseMessage> Sign(HttpClient signer, string approval, string reauthToken, string version = "\"1\"") =>
        signer.PostJsonAsync($"/api/v1/approvals/{approval}/sign", new { reauthToken, meaning = "Approval", reason = "Checked the evidence" }, version);

    /// <summary>Requests and signs (by the independent approver) an approval of the given adjustment version.</summary>
    private static async Task<string> ApproveAsync(World w, string adjustment, int version)
    {
        var approval = await RequestApprovalAsync(w.Requester, adjustment, version);
        await (await Sign(w.Approver, approval, TestTokens.Issue(w.ApproverSubject))).ExpectAsync(HttpStatusCode.OK);
        return approval;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Guid key, string ifMatch, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", key.ToString());
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return client.SendAsync(request);
    }

    private async Task<string> BucketAsync(World w, string product, decimal onHand, decimal reserved = 0)
    {
        var bucket = await ScalarAsync<Guid>("""
            INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, quality_status, received_at, on_hand_qty, reserved_qty, created_by)
            VALUES (@t, @w, @b, @p, 'Available', now(), @q, @r, 'test') RETURNING id
            """, ("t", w.Tenant), ("w", Guid.Parse(w.Warehouse)), ("b", Guid.Parse(w.A01)), ("p", Guid.Parse(product)), ("q", onHand), ("r", reserved));
        await ExecAsync("""
            INSERT INTO ti.stock_movement (tenant_id, movement_type, operation_key, product_id, destination_bucket_id, base_qty, document_type, document_id,
                                           actor_subject, occurred_at)
            VALUES (@t, 'Receipt', 'test-receipt:' || gen_random_uuid(), @p, @b, @q, 'Receipt', gen_random_uuid(), 'test', now())
            """, ("t", w.Tenant), ("p", Guid.Parse(product)), ("b", bucket), ("q", onHand));
        return bucket.ToString();
    }

    private async Task<string> LotAsync(World w, string product, string number) => (await ScalarAsync<Guid>("""
        INSERT INTO ti.lot (tenant_id, product_id, handling_profile, lot_number, expiry_date, created_by)
        SELECT @t, id, handling_profile, @n, current_date + 200, 'test' FROM ti.product WHERE id = @p RETURNING id
        """, ("t", w.Tenant), ("p", Guid.Parse(product)), ("n", number))).ToString();

    private async Task<string> AttachmentAsync(World w) => (await ScalarAsync<Guid>("""
        INSERT INTO ti.attachment (tenant_id, file_name, object_key, media_type, size_bytes, digest_sha256, scan_status, created_by)
        VALUES (@t, 'count-sheet.pdf', @k, 'application/pdf', 2048, @d, 'Clean', 'test') RETURNING id
        """, ("t", w.Tenant), ("k", $"{w.Tenant}/{Guid.NewGuid()}.pdf"), ("d", RandomNumberGenerator.GetBytes(32)))).ToString();

    private async Task<string> QtyAsync(string bucket) => await ScalarAsync<string>(
        "SELECT trim_scale(on_hand_qty)::text || '|' || trim_scale(reserved_qty)::text FROM ti.stock_bucket WHERE id = @b", ("b", Guid.Parse(bucket)));

    private static async Task<List<JsonObject>> ItemsAsync(HttpClient client, string url) =>
        (await (await client.GetAsync(url)).ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray().Select(i => i!.AsObject()).ToList();

    private async Task<long> CountAsync(string sql, Guid tenant) => await ScalarAsync<long>(sql, ("t", tenant));

    private Task<int> ExecAsync(string sql, params (string Name, object Value)[] args) => TenantProvisioning.ExecAsSuperuserAsync(fx, sql, args);

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args) =>
        (T)(await TenantProvisioning.ScalarAsSuperuserAsync(fx, sql, args))!;
}
