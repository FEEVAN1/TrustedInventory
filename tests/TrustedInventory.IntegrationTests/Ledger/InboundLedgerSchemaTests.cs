using System.Net;
using Npgsql;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Ledger;

/// <summary>
/// Database guarantees of the inbound ledger (migration 0006), proven by attempting impossible states directly as the
/// superuser. These are the last line of defence; M2 services reject the same states earlier with API errors.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class InboundLedgerSchemaTests(ApiFixture fx)
{
    private const string CheckViolation = PostgresErrorCodes.CheckViolation;
    private const string UniqueViolation = PostgresErrorCodes.UniqueViolation;

    [Fact]
    [Trait("Requirement", "STK-004")]
    [Trait("Requirement", "DATA-08")]
    public async Task The_movement_ledger_and_receipt_lines_are_append_only_for_every_role()
    {
        var w = await WorldAsync("ledger-append");
        var bucket = await BucketAsync(w, w.General, w.Bin1, onHand: 10);
        var movement = await MovementAsync(w, "Receipt", w.General, source: null, destination: bucket, qty: 10);

        // The runtime role was never granted UPDATE or DELETE on append-only tables, nor DELETE on any ledger table. The one
        // DELETE it holds, on draft purchase order lines, is guarded by the 0007 trigger (DEC-047).
        var granted = await ListAsync("""
            SELECT t || ':' || p FROM unnest(ARRAY['stock_movement','document_operation','receipt_line','receipt_line_serial']) t,
                 unnest(ARRAY['UPDATE','DELETE','TRUNCATE']) p WHERE has_table_privilege('ti_app', 'ti.' || t, p)
            UNION ALL
            SELECT t || ':DELETE' FROM unnest(ARRAY['lot','serial','stock_bucket','purchase_order','receipt','warehouse_task']) t
            WHERE has_table_privilege('ti_app', 'ti.' || t, 'DELETE')
            """);
        Assert.Empty(granted);

        // Even a superuser hits the append-only trigger.
        await ExpectAsync(PostgresErrorCodes.InsufficientPrivilege, "UPDATE ti.stock_movement SET base_qty = 1 WHERE id = @id", ("id", movement));
        await ExpectAsync(PostgresErrorCodes.InsufficientPrivilege, "DELETE FROM ti.stock_movement WHERE id = @id", ("id", movement));
    }

    [Fact]
    [Trait("Requirement", "STK-001")]
    [Trait("Requirement", "STK-PRECISION")]
    [Trait("Requirement", "DATA-08")]
    public async Task Stock_buckets_reject_impossible_quantities()
    {
        var w = await WorldAsync("ledger-bucket");
        await ExpectAsync(CheckViolation, BucketSql, BucketArgs(w, w.General, w.Bin1, onHand: 10m, reserved: 11m));   // reserved > on hand
        await ExpectAsync(CheckViolation, BucketSql, BucketArgs(w, w.General, w.Bin1, onHand: 1.1234567m, reserved: 0m)); // 7 decimals
        await ExpectAsync(CheckViolation, BucketSql, BucketArgs(w, w.General, w.Bin1, onHand: -1m, reserved: 0m));

        // A serial unit is 0 or 1, and on hand in at most one bucket.
        var serial = await SerialAsync(w, "SN-1");
        await ExpectAsync(CheckViolation, BucketSql, BucketArgs(w, w.Serialized, w.Bin1, onHand: 2m, reserved: 0m, serial: serial));
        await Exec(BucketSql, BucketArgs(w, w.Serialized, w.Bin1, onHand: 1m, reserved: 0m, serial: serial));
        await ExpectAsync(UniqueViolation, BucketSql, BucketArgs(w, w.Serialized, w.Bin2, onHand: 1m, reserved: 0m, serial: serial));
    }

    [Fact]
    [Trait("Requirement", "INB-004")]
    [Trait("Requirement", "DATA-08")]
    public async Task Serials_are_unique_per_product_received_once_and_medical_lots_need_expiry()
    {
        var w = await WorldAsync("ledger-serial");
        var serial = await SerialAsync(w, "SN-DUP");
        await ExpectAsync(UniqueViolation, "INSERT INTO ti.serial (tenant_id, product_id, serial_number, created_by) VALUES (@t, @p, 'SN-DUP', 'test')",
            ("t", w.Tenant), ("p", w.Serialized));

        // The same serial cannot be received on two receipt lines.
        var line = await ReceiptLineAsync(w, w.Serialized, qty: 1);
        await Exec("INSERT INTO ti.receipt_line_serial (tenant_id, receipt_line_id, serial_id, created_by) VALUES (@t, @l, @s, 'test')",
            ("t", w.Tenant), ("l", line), ("s", serial));
        var second = await ReceiptLineAsync(w, w.Serialized, qty: 1);
        await ExpectAsync(UniqueViolation, "INSERT INTO ti.receipt_line_serial (tenant_id, receipt_line_id, serial_id, created_by) VALUES (@t, @l, @s, 'test')",
            ("t", w.Tenant), ("l", second), ("s", serial));

        // Medical lots carry an expiry; general lots may not.
        await ExpectAsync(CheckViolation, LotSql, ("t", w.Tenant), ("p", w.Medical), ("h", "Medical"), ("n", "MED-1"), ("e", DBNull.Value));
        await Exec(LotSql, ("t", w.Tenant), ("p", w.General), ("h", "General"), ("n", "GEN-1"), ("e", DBNull.Value));
        // A lot cannot claim a handling profile its product does not have.
        await ExpectAsync(PostgresErrorCodes.ForeignKeyViolation, LotSql, ("t", w.Tenant), ("p", w.General), ("h", "Medical"), ("n", "GEN-2"),
            ("e", new DateOnly(2030, 1, 1)));
    }

    [Fact]
    [Trait("Requirement", "INB-001")]
    public async Task Purchase_order_lines_change_only_while_the_order_is_draft()
    {
        var w = await WorldAsync("ledger-draft");
        var (po, line) = await PurchaseOrderAsync(w, w.General, ordered: 10);
        var other = await PurchaseOrderAsync(w, w.General, ordered: 3);

        // Draft: lines may be edited and deleted (the runtime role holds DELETE for this, DEC-047).
        await Exec("UPDATE ti.purchase_order_line SET ordered_uom_qty = 12, ordered_base_qty = 12 WHERE id = @l", ("l", line));
        await Exec("DELETE FROM ti.purchase_order_line WHERE id = @l", ("l", other.Line));
        Assert.Equal(true, await Scalar("SELECT has_table_privilege('ti_app', 'ti.purchase_order_line', 'DELETE')"));

        // Approved: ordered fields, deletes and new lines are refused; receiving progress still records.
        await Exec("UPDATE ti.purchase_order SET status = 'Approved', approved_by = 'test', approved_at = now() WHERE id = @po", ("po", po));
        await ExpectAsync(CheckViolation, "UPDATE ti.purchase_order_line SET ordered_uom_qty = 20, ordered_base_qty = 20 WHERE id = @l", ("l", line));
        await ExpectAsync(CheckViolation, "UPDATE ti.purchase_order_line SET product_id = @p WHERE id = @l", ("l", line), ("p", w.Serialized));
        await ExpectAsync(CheckViolation, "DELETE FROM ti.purchase_order_line WHERE id = @l", ("l", line));
        await ExpectAsync(CheckViolation, """
            INSERT INTO ti.purchase_order_line (tenant_id, purchase_order_id, line_number, product_id, uom_id, uom_factor_to_base,
                                                ordered_uom_qty, ordered_base_qty, created_by)
            VALUES (@t, @po, 2, @p, @u, 1, 1, 1, 'test')
            """, ("t", w.Tenant), ("po", po), ("p", w.General), ("u", w.Unit));
        await Exec("UPDATE ti.purchase_order_line SET received_base_qty = 5, updated_at = now(), version = version + 1 WHERE id = @l", ("l", line));
    }

    [Fact]
    [Trait("Requirement", "INB-001")]
    [Trait("Requirement", "AT-INB-02")]
    public async Task Purchase_order_lines_cannot_be_over_received_and_short_close_needs_a_reason()
    {
        var w = await WorldAsync("ledger-po");
        var (po, line) = await PurchaseOrderAsync(w, w.General, ordered: 50);

        await Exec("UPDATE ti.purchase_order_line SET received_base_qty = 50 WHERE id = @l", ("l", line));
        await ExpectAsync(CheckViolation, "UPDATE ti.purchase_order_line SET received_base_qty = 51 WHERE id = @l", ("l", line)); // zero tolerance (D08)
        await ExpectAsync(CheckViolation, "UPDATE ti.purchase_order SET status = 'ShortClosed' WHERE id = @po", ("po", po));
    }

    [Fact]
    [Trait("Requirement", "STK-004")]
    [Trait("Requirement", "STK-007")]
    [Trait("Requirement", "STK-PRECISION")]
    public async Task Movements_have_a_valid_direction_a_positive_quantity_and_a_permanent_key()
    {
        var w = await WorldAsync("ledger-move");
        var staging = await BucketAsync(w, w.General, w.Bin1, onHand: 10);
        var storage = await BucketAsync(w, w.General, w.Bin2, onHand: 0);

        await ExpectAsync(CheckViolation, MovementSql, MovementArgs(w, "Receipt", w.General, staging, storage, 1m, "k1"));   // receipt has no source
        await ExpectAsync(CheckViolation, MovementSql, MovementArgs(w, "Putaway", w.General, null, storage, 1m, "k2"));      // putaway needs both
        await ExpectAsync(CheckViolation, MovementSql, MovementArgs(w, "Putaway", w.General, staging, staging, 1m, "k3"));   // never to itself
        await ExpectAsync(CheckViolation, MovementSql, MovementArgs(w, "Putaway", w.General, staging, storage, 0m, "k4"));   // positive only
        await ExpectAsync(CheckViolation, MovementSql, MovementArgs(w, "Pick", w.General, staging, storage, 1m, "k5"));      // pick needs a reservation
        await ExpectAsync(CheckViolation, MovementSql, MovementArgs(w, "Reversal", w.General, storage, staging, 1m, "k6")); // reversal names its original

        await Exec(MovementSql, MovementArgs(w, "Putaway", w.General, staging, storage, 4m, "putaway:once"));
        await ExpectAsync(UniqueViolation, MovementSql, MovementArgs(w, "Putaway", w.General, staging, storage, 4m, "putaway:once")); // STK-007
    }

    // ---------------------------------------------------------------------------------------------------------------
    // A tenant with one warehouse, two storage bins, a unit, a supplier and three products (general, serialized, medical).
    // ---------------------------------------------------------------------------------------------------------------

    private sealed record World(Guid Tenant, Guid Warehouse, Guid Bin1, Guid Bin2, Guid Unit, Guid Supplier, Guid General, Guid Serialized, Guid Medical);

    private async Task<World> WorldAsync(string label)
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, label);
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        async Task<Guid> Create(string path, object body) =>
            Guid.Parse((string)(await (await admin.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!);

        var warehouse = await Create("/api/v1/warehouses", new { code = "W1", name = "Main", timeZone = "UTC" });
        var zone = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "Z1", name = "Ambient" });
        var bin1 = await Create("/api/v1/bins", new { zoneId = zone, code = "B1", kind = "Storage" });
        var bin2 = await Create("/api/v1/bins", new { zoneId = zone, code = "B2", kind = "Storage" });
        var unit = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        var supplier = await Create("/api/v1/partners", new { code = "SUP", name = "Supplier", type = "Supplier" });
        var profile = await Create("/api/v1/storage-profiles", new
        {
            name = "Ambient medical", minC = 15, maxC = 25, monitoringRequired = false,
            excursionPolicy = new { immediateHold = true, notes = "Synthetic policy" }, reason = "CFG-002 needs a profile (DEC-100)",
        });
        object Product(string sku, string handling, string tracking) =>
            new { sku, name = sku, handlingProfile = handling, trackingMode = tracking, baseUomId = unit, basePrecision = 0,
                  storageProfileId = handling == "Medical" ? (object)profile : null };
        var general = await Create("/api/v1/products", Product("GEN", "General", "Quantity"));
        var serialized = await Create("/api/v1/products", Product("SER", "General", "Serial"));
        var medical = await Create("/api/v1/products", Product("MED", "Medical", "Lot"));
        return new World(tenant.TenantId, warehouse, bin1, bin2, unit, supplier, general, serialized, medical);
    }

    private const string BucketSql = """
        INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, serial_id, quality_status, received_at, on_hand_qty, reserved_qty, created_by)
        VALUES (@t, @w, @b, @p, @s, 'Available', now(), @on, @res, 'test') RETURNING id
        """;

    private static (string, object)[] BucketArgs(World w, Guid product, Guid bin, decimal onHand, decimal reserved, Guid? serial = null) =>
        [("t", w.Tenant), ("w", w.Warehouse), ("b", bin), ("p", product), ("s", (object?)serial ?? DBNull.Value), ("on", onHand), ("res", reserved)];

    private async Task<Guid> BucketAsync(World w, Guid product, Guid bin, decimal onHand) =>
        (Guid)(await Scalar(BucketSql, BucketArgs(w, product, bin, onHand, 0m)))!;

    private const string MovementSql = """
        INSERT INTO ti.stock_movement (tenant_id, movement_type, operation_key, product_id, source_bucket_id, destination_bucket_id,
                                       base_qty, document_type, document_id, actor_subject, occurred_at)
        VALUES (@t, @type::ti.movement_type, @key, @p, @src, @dst, @q, 'Receipt', gen_random_uuid(), 'test', now()) RETURNING id
        """;

    private static (string, object)[] MovementArgs(World w, string type, Guid product, Guid? source, Guid? destination, decimal qty, string key) =>
        [("t", w.Tenant), ("type", type), ("key", key), ("p", product), ("src", (object?)source ?? DBNull.Value),
         ("dst", (object?)destination ?? DBNull.Value), ("q", qty)];

    private async Task<Guid> MovementAsync(World w, string type, Guid product, Guid? source, Guid? destination, decimal qty) =>
        (Guid)(await Scalar(MovementSql, MovementArgs(w, type, product, source, destination, qty, $"test:{Guid.NewGuid():N}")))!;

    private const string LotSql = """
        INSERT INTO ti.lot (tenant_id, product_id, handling_profile, lot_number, expiry_date, created_by)
        VALUES (@t, @p, @h::ti.handling_profile, @n, @e, 'test')
        """;

    private async Task<Guid> SerialAsync(World w, string number) =>
        (Guid)(await Scalar("INSERT INTO ti.serial (tenant_id, product_id, serial_number, created_by) VALUES (@t, @p, @n, 'test') RETURNING id",
            ("t", w.Tenant), ("p", w.Serialized), ("n", number)))!;

    private async Task<(Guid Po, Guid Line)> PurchaseOrderAsync(World w, Guid product, decimal ordered)
    {
        var po = (Guid)(await Scalar("""
            INSERT INTO ti.purchase_order (tenant_id, supplier_id, warehouse_id, reference, created_by)
            VALUES (@t, @s, @w, @r, 'test') RETURNING id
            """, ("t", w.Tenant), ("s", w.Supplier), ("w", w.Warehouse), ("r", $"PO-{Guid.NewGuid():N}"[..20])))!;
        var line = (Guid)(await Scalar("""
            INSERT INTO ti.purchase_order_line (tenant_id, purchase_order_id, line_number, product_id, uom_id, uom_factor_to_base,
                                                ordered_uom_qty, ordered_base_qty, created_by)
            VALUES (@t, @po, 1, @p, @u, 1, @q, @q, 'test') RETURNING id
            """, ("t", w.Tenant), ("po", po), ("p", product), ("u", w.Unit), ("q", ordered)))!;
        return (po, line);
    }

    private async Task<Guid> ReceiptLineAsync(World w, Guid product, decimal qty)
    {
        var (po, poLine) = await PurchaseOrderAsync(w, product, qty);
        var receipt = (Guid)(await Scalar("""
            INSERT INTO ti.receipt (tenant_id, purchase_order_id, warehouse_id, status, received_at, created_by)
            VALUES (@t, @po, @w, 'Posted', now(), 'test') RETURNING id
            """, ("t", w.Tenant), ("po", po), ("w", w.Warehouse)))!;
        // Receipts add to the bucket for this bin/product/lot/serial/quality; it exists at most once (natural key).
        var bucket = (Guid?)await Scalar("""
            SELECT id FROM ti.stock_bucket WHERE tenant_id = @t AND bin_id = @b AND product_id = @p
              AND lot_id IS NULL AND serial_id IS NULL AND quality_status = 'Available'
            """, ("t", w.Tenant), ("b", w.Bin1), ("p", product)) ?? await BucketAsync(w, product, w.Bin1, onHand: 0);
        return (Guid)(await Scalar("""
            INSERT INTO ti.receipt_line (tenant_id, receipt_id, purchase_order_line_id, product_id, uom_id, uom_factor_to_base, uom_qty, base_qty,
                                         staging_bin_id, warehouse_id, quality_status, bucket_id, created_by)
            VALUES (@t, @r, @l, @p, @u, 1, @q, @q, @b, @w, 'Available', @bucket, 'test') RETURNING id
            """, ("t", w.Tenant), ("r", receipt), ("l", poLine), ("p", product), ("u", w.Unit), ("q", qty), ("b", w.Bin1),
            ("w", w.Warehouse), ("bucket", bucket)))!;
    }

    // ---------------------------------------------------------------------------------------------------------------

    private async Task ExpectAsync(string sqlState, string sql, params (string Name, object Value)[] args)
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

    private async Task<List<string>> ListAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }
}
