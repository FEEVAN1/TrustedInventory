using System.Net;
using System.Security.Cryptography;
using Npgsql;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Operations;

/// <summary>
/// Database guarantees of the general-operations schema (migration 0013, DEC-056), proven by attempting impossible
/// states directly as the superuser. M4 services reject the same states earlier with API errors.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class GeneralOperationsSchemaTests(ApiFixture fx)
{
    private const string Check = PostgresErrorCodes.CheckViolation;
    private const string Unique = PostgresErrorCodes.UniqueViolation;

    private static readonly string[] Tables =
    [
        "approval", "attachment", "transfer", "transfer_line", "stock_count", "stock_count_bin", "stock_count_line", "adjustment",
        "customer_return", "customer_return_inspection", "export_job",
    ];

    [Fact]
    [Trait("Requirement", "TEN-001")]
    [Trait("Requirement", "TEN-002")]
    [Trait("Requirement", "DATA-08")]
    public async Task Every_new_table_forces_row_level_security_keeps_history_and_has_no_delete()
    {
        var unprotected = await ListAsync("""
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'ti' AND c.relname = ANY(@t) AND NOT (c.relrowsecurity AND c.relforcerowsecurity)
            """, ("t", Tables));
        Assert.Empty(unprotected);
        Assert.Equal(Tables.Length, (int)(long)(await Scalar("""
            SELECT count(*) FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid WHERE c.relname = ANY(@t) AND p.polname = 'tenant_isolation'
            """, ("t", Tables)))!);
        var deletable = await ListAsync("SELECT t FROM unnest(@t) t WHERE has_table_privilege('ti_app', 'ti.' || t, 'DELETE')", ("t", Tables));
        Assert.Empty(deletable);
        Assert.Equal(false, await Scalar("SELECT has_table_privilege('ti_app', 'ti.customer_return_inspection', 'UPDATE')"));
        Assert.Equal(2L, await Scalar("""
            SELECT count(*) FROM pg_trigger WHERE tgrelid = 'ti.customer_return_inspection'::regclass
              AND tgname IN ('customer_return_inspection_append_only', 'customer_return_inspection_no_truncate')
            """));
    }

    [Fact]
    [Trait("Requirement", "IAM-003")]
    [Trait("Requirement", "SOD-1")]
    [Trait("Requirement", "IAM-004")]
    public async Task Approvals_bind_their_subject_refuse_self_signing_expire_in_30_minutes_and_are_consumed_once()
    {
        var w = await WorldAsync("schema-approval");
        await Expect(Check, Approval("'Approved'", approver: "'requester'"), ("t", w.Tenant), ("d", Digest()));          // SOD-1
        await Expect(Check, Approval("'Approved'", approver: "'signer'", reauth: false), ("t", w.Tenant), ("d", Digest())); // signature without re-auth
        await Expect(Check, Approval("'Approved'", approver: "'signer'", expiresMinutes: 60), ("t", w.Tenant), ("d", Digest()));
        await Expect(Check, "UPDATE ti.approval SET status = 'Rejected' WHERE id = @id", ("id", await PendingAsync(w)));  // rejection needs who and why

        var pending = await PendingAsync(w);
        await Expect(Check, "UPDATE ti.approval SET subject_version = 2 WHERE id = @id", ("id", pending));               // binding frozen
        await Expect(Check, "UPDATE ti.approval SET content_digest = @d WHERE id = @id", ("id", pending), ("d", Digest()));
        await Exec("""
            UPDATE ti.approval SET status = 'Approved', approver_subject = 'signer', meaning = 'Approval', sign_reason = 'checked',
              reauth_reference = @d, reauth_verified_at = now(), signed_at = now(), expires_at = now() + interval '30 minutes'
            WHERE id = @id
            """, ("id", pending), ("d", Digest()));
        await Expect(Check, "UPDATE ti.approval SET approver_subject = 'someone-else' WHERE id = @id", ("id", pending));   // signature frozen
        await Expect(Check, "UPDATE ti.approval SET status = 'Pending' WHERE id = @id", ("id", pending));                // no way back
        await Exec("UPDATE ti.approval SET status = 'Consumed', consumed_at = now(), consumed_operation = 'adjustment:post:1' WHERE id = @id",
            ("id", pending));
        await Expect(Check, "UPDATE ti.approval SET consumed_operation = 'again' WHERE id = @id", ("id", pending));       // consumed is final

        var second = await PendingAsync(w);
        await Exec("""
            UPDATE ti.approval SET status = 'Approved', approver_subject = 'signer', meaning = 'Approval', sign_reason = 'checked',
              reauth_reference = @d, reauth_verified_at = now(), signed_at = now(), expires_at = now() + interval '30 minutes'
            WHERE id = @id
            """, ("id", second), ("d", Digest()));
        await Expect(Unique, "UPDATE ti.approval SET status = 'Consumed', consumed_at = now(), consumed_operation = 'adjustment:post:1' WHERE id = @id",
            ("id", second));                                                                                                  // one approval, one posting
        await Expect(Check, "UPDATE ti.approval SET status = 'Invalidated' WHERE id = @id", ("id", second));             // needs a reason
        await Exec("UPDATE ti.approval SET status = 'Invalidated', invalidated_reason = 'subject changed' WHERE id = @id", ("id", second));
    }

    [Fact]
    [Trait("Requirement", "EXC-004")]
    public async Task Adjustments_need_a_bucket_to_reduce_an_approval_to_post_a_movement_when_posted_and_never_change_afterwards()
    {
        var w = await WorldAsync("schema-adjust");
        var bucket = await BucketAsync(w, 10);
        await Expect(Check, Adjustment("NULL", -2), ("t", w.Tenant), ("w", w.Warehouse), ("bin", w.Storage), ("p", w.Product));    // reduction without bucket
        await Expect(Check, Adjustment("NULL", 0), ("t", w.Tenant), ("w", w.Warehouse), ("bin", w.Storage), ("p", w.Product));     // zero delta
        var adjustment = (Guid)(await Scalar(Adjustment("@b", -2) + " RETURNING id",
            ("t", w.Tenant), ("w", w.Warehouse), ("bin", w.Storage), ("p", w.Product), ("b", bucket)))!;
        await Expect(Check, "UPDATE ti.adjustment SET status = 'Approved' WHERE id = @id", ("id", adjustment));          // approval required
        var approval = await PendingAsync(w);
        await Expect(Check, "UPDATE ti.adjustment SET status = 'Posted', approval_id = @a WHERE id = @id", ("id", adjustment), ("a", approval));
        var movement = await MovementAsync(w, bucket);
        await Exec("UPDATE ti.adjustment SET status = 'Posted', approval_id = @a, posted_movement_id = @m WHERE id = @id",
            ("id", adjustment), ("a", approval), ("m", movement));
        await Expect(Check, "UPDATE ti.adjustment SET narrative = 'rewritten' WHERE id = @id", ("id", adjustment));     // posted is frozen
    }

    [Fact]
    [Trait("Requirement", "EXC-001")]
    [Trait("Requirement", "STK-003")]
    public async Task Transfers_cross_warehouses_and_their_lines_never_receive_or_dispose_more_than_was_dispatched()
    {
        var w = await WorldAsync("schema-transfer");
        await Expect(Check, """
            INSERT INTO ti.transfer (tenant_id, source_warehouse_id, destination_warehouse_id, reference, created_by, last_modified_by) VALUES (@t, @w, @w, 'TR-1', 'test', 'test')
            """, ("t", w.Tenant), ("w", w.Warehouse));
        var transfer = (Guid)(await Scalar("""
            INSERT INTO ti.transfer (tenant_id, source_warehouse_id, destination_warehouse_id, reference, created_by, last_modified_by) VALUES (@t, @w, @d, 'TR-1', 'test', 'test')
            RETURNING id
            """, ("t", w.Tenant), ("w", w.Warehouse), ("d", w.Other)))!;
        await Expect(Check, "UPDATE ti.transfer SET status = 'Approved' WHERE id = @id", ("id", transfer));               // who approved, when
        await Expect(Check, "UPDATE ti.transfer SET status = 'InTransit', approved_by = 'a', approved_at = now() WHERE id = @id", ("id", transfer));
        var line = (Guid)(await Scalar("""
            INSERT INTO ti.transfer_line (tenant_id, transfer_id, line_number, product_id, requested_qty, created_by) VALUES (@t, @tr, 1, @p, 20, 'test')
            RETURNING id
            """, ("t", w.Tenant), ("tr", transfer), ("p", w.Product)))!;
        await Expect(Check, "UPDATE ti.transfer_line SET dispatched_qty = 21 WHERE id = @l", ("l", line));
        await Exec("UPDATE ti.transfer_line SET dispatched_qty = 20, received_qty = 18 WHERE id = @l", ("l", line));      // AT-EXC-01 shape
        await Expect(Check, "UPDATE ti.transfer_line SET disposed_qty = 3 WHERE id = @l", ("l", line));                  // 18 + 3 > 20
        await Exec("UPDATE ti.transfer_line SET disposed_qty = 2 WHERE id = @l", ("l", line));
    }

    [Fact]
    [Trait("Requirement", "EXC-003")]
    public async Task A_bin_has_at_most_one_active_count_lock_and_variance_is_derived_from_the_snapshot()
    {
        var w = await WorldAsync("schema-count");
        var first = await CountAsync(w);
        var second = await CountAsync(w);
        await Exec("INSERT INTO ti.stock_count_bin (tenant_id, count_id, warehouse_id, bin_id, lock_active, created_by) VALUES (@t, @c, @w, @b, true, 'test')",
            ("t", w.Tenant), ("c", first), ("w", w.Warehouse), ("b", w.Storage));
        await Expect(Unique, "INSERT INTO ti.stock_count_bin (tenant_id, count_id, warehouse_id, bin_id, lock_active, created_by) VALUES (@t, @c, @w, @b, true, 'test')",
            ("t", w.Tenant), ("c", second), ("w", w.Warehouse), ("b", w.Storage));
        await Expect(Check, "UPDATE ti.stock_count SET status = 'Counting' WHERE id = @c", ("c", first));                // locked_at required
        var line = (Guid)(await Scalar("""
            INSERT INTO ti.stock_count_line (tenant_id, count_id, revision, bin_id, product_id, expected_qty, created_by)
            VALUES (@t, @c, 1, @b, @p, 10, 'test') RETURNING id
            """, ("t", w.Tenant), ("c", first), ("b", w.Storage), ("p", w.Product)))!;
        await Expect(Check, "UPDATE ti.stock_count_line SET observed_qty = 7 WHERE id = @l", ("l", line));               // who counted, when
        await Exec("UPDATE ti.stock_count_line SET observed_qty = 7, counted_by = 'counter', counted_at = now() WHERE id = @l", ("l", line));
        Assert.Equal(-3m, await Scalar("SELECT variance_qty FROM ti.stock_count_line WHERE id = @l", ("l", line)));
        await Expect(Unique, """
            INSERT INTO ti.stock_count_line (tenant_id, count_id, revision, bin_id, product_id, expected_qty, created_by)
            VALUES (@t, @c, 1, @b, @p, 10, 'test')
            """, ("t", w.Tenant), ("c", first), ("b", w.Storage), ("p", w.Product));                                       // NULL lot/serial still unique
    }

    [Fact]
    [Trait("Requirement", "EXC-005")]
    [Trait("Requirement", "AT-EXC-03")]
    public async Task Returns_stay_within_shipped_and_inspected_quantities_and_inspections_are_append_only()
    {
        var w = await WorldAsync("schema-return");
        var quarantine = await BucketAsync(w, 0, bin: w.Quarantine, quality: "Quarantine");
        var movement = await MovementAsync(w, quarantine);
        var shipmentLine = await ShipmentLineAsync(w, movement, shipped: 5);
        await Expect(Check, "UPDATE ti.shipment_line SET returned_qty = 6 WHERE id = @l", ("l", shipmentLine));           // AT-EXC-03 in the database

        var ret = (Guid)(await Scalar("""
            INSERT INTO ti.customer_return (tenant_id, warehouse_id, shipment_line_id, product_id, returned_qty, quarantine_bin_id, bucket_id,
                                            receipt_movement_id, reason, created_by)
            VALUES (@t, @w, @sl, @p, 4, @q, @b, @m, 'Customer refused delivery', 'test') RETURNING id
            """, ("t", w.Tenant), ("w", w.Warehouse), ("sl", shipmentLine), ("p", w.Product), ("q", w.Quarantine), ("b", quarantine), ("m", movement)))!;
        await Expect(Check, "UPDATE ti.customer_return SET inspected_qty = 5, disposition = 'Release', status = 'Inspected' WHERE id = @r", ("r", ret));
        await Expect(Check, "UPDATE ti.customer_return SET inspected_qty = 1 WHERE id = @r", ("r", ret));                 // disposition with it
        await Expect(Check, "UPDATE ti.customer_return SET inspected_qty = 4, disposition = 'Release' WHERE id = @r", ("r", ret)); // fully inspected = Inspected
        await Exec("UPDATE ti.customer_return SET inspected_qty = 1, disposition = 'Damaged' WHERE id = @r", ("r", ret));
        var inspection = (Guid)(await Scalar("""
            INSERT INTO ti.customer_return_inspection (tenant_id, return_id, disposition, qty, reason, movement_id, created_by)
            VALUES (@t, @r, 'Damaged', 1, 'Crushed box', @m, 'test') RETURNING id
            """, ("t", w.Tenant), ("r", ret), ("m", movement)))!;
        await Expect(PostgresErrorCodes.InsufficientPrivilege, "UPDATE ti.customer_return_inspection SET qty = 2 WHERE id = @i", ("i", inspection));
        await Expect(PostgresErrorCodes.InsufficientPrivilege, "DELETE FROM ti.customer_return_inspection WHERE id = @i", ("i", inspection));
    }

    [Fact]
    [Trait("Requirement", "TEN-005")]
    [Trait("Requirement", "AUD-004")]
    public async Task Attachments_and_exports_live_under_the_tenant_prefix_and_completed_exports_carry_a_manifest()
    {
        var w = await WorldAsync("schema-files");
        const string Insert = """
            INSERT INTO ti.attachment (tenant_id, file_name, object_key, media_type, size_bytes, digest_sha256, created_by)
            VALUES (@t, 'evidence.pdf', @k, @mt, @s, @d, 'test')
            """;
        await Expect(Check, Insert, ("t", w.Tenant), ("k", $"{Guid.NewGuid()}/evidence.pdf"), ("mt", "application/pdf"), ("s", 100L), ("d", Digest()));
        await Expect(Check, Insert, ("t", w.Tenant), ("k", $"{w.Tenant}/a.exe"), ("mt", "application/x-msdownload"), ("s", 100L), ("d", Digest()));
        await Expect(Check, Insert, ("t", w.Tenant), ("k", $"{w.Tenant}/big.pdf"), ("mt", "application/pdf"), ("s", 10485761L), ("d", Digest()));
        await Exec(Insert, ("t", w.Tenant), ("k", $"{w.Tenant}/evidence.pdf"), ("mt", "application/pdf"), ("s", 100L), ("d", Digest()));

        var job = (Guid)(await Scalar("INSERT INTO ti.export_job (tenant_id, type, filter, filter_digest, created_by) VALUES (@t, 'Movements', '{}', @d, 'test') RETURNING id",
            ("t", w.Tenant), ("d", Digest())))!;
        await Expect(Check, "UPDATE ti.export_job SET status = 'Completed', completed_at = now() WHERE id = @j", ("j", job));
        await Expect(Check, "UPDATE ti.export_job SET status = 'Completed', completed_at = now(), manifest = '{}', object_key = @k WHERE id = @j",
            ("j", job), ("k", $"{Guid.NewGuid()}/export.csv"));
        await Expect(Check, "UPDATE ti.export_job SET status = 'Failed' WHERE id = @j", ("j", job));                       // failure needs a reason
        await Exec("UPDATE ti.export_job SET status = 'Completed', completed_at = now(), manifest = '{}', object_key = @k WHERE id = @j",
            ("j", job), ("k", $"{w.Tenant}/export.csv"));
    }

    // ---------------------------------------------------------------------------------------------------------------

    private sealed record World(Guid Tenant, Guid Warehouse, Guid Other, Guid Storage, Guid Quarantine, Guid Product, Guid Customer);

    private async Task<World> WorldAsync(string label)
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, label);
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        async Task<Guid> Create(string path, object body) =>
            Guid.Parse((string)(await (await admin.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!);
        var warehouse = await Create("/api/v1/warehouses", new { code = "W1", name = "Main", timeZone = "UTC" });
        var other = await Create("/api/v1/warehouses", new { code = "W2", name = "Second", timeZone = "UTC" });
        var zone = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "Z1", name = "Ambient" });
        var storage = await Create("/api/v1/bins", new { zoneId = zone, code = "A-01", kind = "Storage" });
        var quarantine = await Create("/api/v1/bins", new { zoneId = zone, code = "Q-01", kind = "Quarantine" });
        var unit = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        var product = await Create("/api/v1/products", new { sku = "GEN", name = "GEN", handlingProfile = "General", trackingMode = "Quantity", baseUomId = unit, basePrecision = 0 });
        var customer = await Create("/api/v1/partners", new { code = "CUS", name = "Customer", type = "Customer" });
        return new World(tenant.TenantId, warehouse, other, storage, quarantine, product, customer);
    }

    private static byte[] Digest() => RandomNumberGenerator.GetBytes(32);

    private static string Approval(string status, string approver, bool reauth = true, int expiresMinutes = 30) => $"""
        INSERT INTO ti.approval (tenant_id, subject_type, subject_id, subject_version, content_digest, action, request_reason, requester_subject,
                                 status, approver_subject, meaning, sign_reason, reauth_reference, reauth_verified_at, signed_at, expires_at, created_by)
        VALUES (@t, 'Adjustment', gen_random_uuid(), 1, @d, 'Post', 'please post', 'requester', {status}, {approver}, 'Approval', 'checked',
                {(reauth ? "@d" : "NULL")}, {(reauth ? "now()" : "NULL")}, now(), now() + interval '{expiresMinutes} minutes', 'requester')
        """;

    private async Task<Guid> PendingAsync(World w) => (Guid)(await Scalar("""
        INSERT INTO ti.approval (tenant_id, subject_type, subject_id, subject_version, content_digest, action, request_reason, requester_subject, created_by)
        VALUES (@t, 'Adjustment', gen_random_uuid(), 1, @d, 'Post', 'please post', 'requester', 'requester') RETURNING id
        """, ("t", w.Tenant), ("d", Digest())))!;

    private static string Adjustment(string bucket, decimal delta) => $"""
        INSERT INTO ti.adjustment (tenant_id, warehouse_id, bucket_id, bin_id, product_id, quality_status, delta_qty, reason_code, narrative, created_by,
                                   last_modified_by)
        VALUES (@t, @w, {bucket}, @bin, @p, 'Available', {delta.ToString(System.Globalization.CultureInfo.InvariantCulture)}, 'DAMAGE', 'Broken in aisle', 'test', 'test')
        """;

    private async Task<Guid> BucketAsync(World w, decimal onHand, Guid? bin = null, string quality = "Available") => (Guid)(await Scalar("""
        INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, quality_status, received_at, on_hand_qty, created_by)
        VALUES (@t, @w, @b, @p, @q::ti.quality_status, now(), @qty, 'test') RETURNING id
        """, ("t", w.Tenant), ("w", w.Warehouse), ("b", bin ?? w.Storage), ("p", w.Product), ("q", quality), ("qty", onHand)))!;

    private async Task<Guid> MovementAsync(World w, Guid bucket) => (Guid)(await Scalar("""
        INSERT INTO ti.stock_movement (tenant_id, movement_type, operation_key, product_id, destination_bucket_id, base_qty, document_type, document_id,
                                       actor_subject, occurred_at)
        VALUES (@t, 'AdjustmentIncrease', 'test:' || gen_random_uuid(), @p, @b, 1, 'Adjustment', gen_random_uuid(), 'test', now()) RETURNING id
        """, ("t", w.Tenant), ("p", w.Product), ("b", bucket)))!;

    private async Task<Guid> CountAsync(World w) => (Guid)(await Scalar(
        "INSERT INTO ti.stock_count (tenant_id, warehouse_id, reason, created_by, last_modified_by) VALUES (@t, @w, 'Cycle count', 'test', 'test') RETURNING id",
        ("t", w.Tenant), ("w", w.Warehouse)))!;

    private async Task<Guid> ShipmentLineAsync(World w, Guid movement, decimal shipped)
    {
        var order = (Guid)(await Scalar("""
            INSERT INTO ti.outbound_order (tenant_id, customer_id, warehouse_id, reference, planned_dispatch_date, created_by)
            VALUES (@t, @c, @w, @r, current_date, 'test') RETURNING id
            """, ("t", w.Tenant), ("c", w.Customer), ("w", w.Warehouse), ("r", $"SO-{Guid.NewGuid():N}"[..20])))!;
        var line = (Guid)(await Scalar("""
            INSERT INTO ti.outbound_order_line (tenant_id, order_id, line_number, product_id, requested_base_qty, created_by)
            VALUES (@t, @o, 1, @p, @q, 'test') RETURNING id
            """, ("t", w.Tenant), ("o", order), ("p", w.Product), ("q", shipped)))!;
        var shipment = (Guid)(await Scalar("""
            INSERT INTO ti.shipment (tenant_id, order_id, warehouse_id, dispatch_reference, dispatched_at, created_by)
            VALUES (@t, @o, @w, 'DSP-1', now(), 'test') RETURNING id
            """, ("t", w.Tenant), ("o", order), ("w", w.Warehouse)))!;
        return (Guid)(await Scalar("""
            INSERT INTO ti.shipment_line (tenant_id, shipment_id, order_line_id, product_id, base_qty, movement_id, created_by)
            VALUES (@t, @s, @l, @p, @q, @m, 'test') RETURNING id
            """, ("t", w.Tenant), ("s", shipment), ("l", line), ("p", w.Product), ("q", shipped), ("m", movement)))!;
    }

    private async Task Expect(string sqlState, string sql, params (string Name, object Value)[] args)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Scalar(sql, args));
        Assert.True(ex.SqlState == sqlState, $"Expected SQLSTATE {sqlState}, got {ex.SqlState}: {ex.MessageText}");
    }

    private async Task Exec(string sql, params (string Name, object Value)[] args) => await Scalar(sql, args);

    private async Task<object?> Scalar(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task<List<string>> ListAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }
}
