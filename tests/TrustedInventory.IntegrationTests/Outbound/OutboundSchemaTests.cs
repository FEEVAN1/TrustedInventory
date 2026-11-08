using System.Net;
using Npgsql;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Outbound;

/// <summary>
/// Database guarantees of the outbound schema (migration 0010, DEC-051), proven by attempting impossible states directly
/// as the superuser. M3 services reject the same states earlier with API errors.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class OutboundSchemaTests(ApiFixture fx)
{
    private const string Check = PostgresErrorCodes.CheckViolation;

    [Fact]
    [Trait("Requirement", "STK-002")]
    [Trait("Requirement", "DATA-08")]
    public async Task Order_line_counters_never_exceed_each_other_or_the_request()
    {
        var w = await WorldAsync("out-counters");
        var (_, line) = await OrderAsync(w, requested: 10);
        await Exec("UPDATE ti.outbound_order_line SET allocated_base_qty = 10, picked_base_qty = 6, dispatched_base_qty = 6 WHERE id = @l", ("l", line));
        await Expect(Check, "UPDATE ti.outbound_order_line SET allocated_base_qty = 11 WHERE id = @l", ("l", line));            // over the request
        await Expect(Check, "UPDATE ti.outbound_order_line SET picked_base_qty = 10.5 WHERE id = @l", ("l", line));             // picked over allocated
        await Expect(Check, "UPDATE ti.outbound_order_line SET dispatched_base_qty = 7 WHERE id = @l", ("l", line));            // dispatched over picked
        await Expect(Check, "UPDATE ti.outbound_order_line SET dispatched_base_qty = 1.1234567 WHERE id = @l", ("l", line));    // precision
    }

    [Fact]
    [Trait("Requirement", "OUT-001")]
    public async Task Order_lines_change_only_while_draft_and_a_cancellation_needs_a_reason()
    {
        var w = await WorldAsync("out-draft");
        var (order, line) = await OrderAsync(w, requested: 5);
        var spare = (await OrderLineAsync(w, order, 2, requested: 1));
        await Exec("UPDATE ti.outbound_order_line SET requested_base_qty = 6, min_remaining_shelf_life_days = 30 WHERE id = @l", ("l", line));
        await Exec("DELETE FROM ti.outbound_order_line WHERE id = @l", ("l", spare));

        await Exec("UPDATE ti.outbound_order SET status = 'Confirmed', confirmed_by = 'test', confirmed_at = now() WHERE id = @o", ("o", order));
        await Expect(Check, "UPDATE ti.outbound_order_line SET requested_base_qty = 7 WHERE id = @l", ("l", line));
        await Expect(Check, "UPDATE ti.outbound_order_line SET min_remaining_shelf_life_days = 0 WHERE id = @l", ("l", line));
        await Expect(Check, "DELETE FROM ti.outbound_order_line WHERE id = @l", ("l", line));
        await Expect(Check, """
            INSERT INTO ti.outbound_order_line (tenant_id, order_id, line_number, product_id, requested_base_qty, created_by)
            VALUES (@t, @o, 3, @p, 1, 'test')
            """, ("t", w.Tenant), ("o", order), ("p", w.General));
        await Exec("UPDATE ti.outbound_order_line SET allocated_base_qty = 6, updated_at = now(), version = version + 1 WHERE id = @l", ("l", line)); // counters still move

        await Expect(Check, "UPDATE ti.outbound_order SET status = 'Cancelled' WHERE id = @o", ("o", order));
        await Expect(Check, "UPDATE ti.outbound_order SET cancel_reason = 'no' WHERE id = @o", ("o", order));
        await Expect(Check, "UPDATE ti.outbound_order SET confirmed_by = NULL WHERE id = @o", ("o", order));
        Assert.Equal(true, await Scalar("SELECT has_table_privilege('ti_app', 'ti.outbound_order_line', 'DELETE')"));
    }

    [Fact]
    [Trait("Requirement", "STK-008")]
    [Trait("Requirement", "STK-002")]
    public async Task Only_active_reservations_expire_and_reservations_stay_on_their_product()
    {
        var w = await WorldAsync("out-reserve");
        var (_, line) = await OrderAsync(w, requested: 5);
        var bucket = await BucketAsync(w, w.General, 10);
        var other = await BucketAsync(w, w.Lotted, 10, lot: true);
        const string insert = """
            INSERT INTO ti.reservation (tenant_id, order_line_id, product_id, bucket_id, qty, state, expires_at, created_by)
            VALUES (@t, @l, @p, @b, 5, @s::ti.reservation_state, @e, 'test') RETURNING id
            """;
        var active = (Guid)(await Scalar(insert, ("t", w.Tenant), ("l", line), ("p", w.General), ("b", bucket), ("s", "Active"), ("e", DateTimeOffset.UtcNow.AddHours(24))))!;
        await Expect(Check, insert, ("t", w.Tenant), ("l", line), ("p", w.General), ("b", bucket), ("s", "Active"), ("e", DBNull.Value));     // active needs an expiry
        await Expect(Check, insert, ("t", w.Tenant), ("l", line), ("p", w.General), ("b", bucket), ("s", "Picked"), ("e", DateTimeOffset.UtcNow)); // picked never expires
        await Expect(PostgresErrorCodes.ForeignKeyViolation, insert, ("t", w.Tenant), ("l", line), ("p", w.General), ("b", other), ("s", "Active"),
            ("e", DateTimeOffset.UtcNow.AddHours(24)));                                                                                            // bucket of another product
        await Exec("UPDATE ti.reservation SET state = 'Picked', expires_at = NULL WHERE id = @r", ("r", active));

        // Pick movements must name an existing reservation (deferred 0006 foreign key).
        const string pick = """
            INSERT INTO ti.stock_movement (tenant_id, movement_type, operation_key, product_id, source_bucket_id, destination_bucket_id, base_qty,
                                           document_type, document_id, reservation_id, actor_subject, occurred_at)
            VALUES (@t, 'Pick', @k, @p, @src, @dst, 1, 'OutboundOrder', gen_random_uuid(), @r, 'test', now())
            """;
        var staging = await BucketAsync(w, w.General, 0, staging: true);
        await Expect(PostgresErrorCodes.ForeignKeyViolation, pick, ("t", w.Tenant), ("k", "pick:x"), ("p", w.General), ("src", bucket), ("dst", staging), ("r", Guid.NewGuid()));
        await Exec(pick, ("t", w.Tenant), ("k", "pick:y"), ("p", w.General), ("src", bucket), ("dst", staging), ("r", active));
    }

    [Fact]
    [Trait("Requirement", "OUT-004")]
    [Trait("Requirement", "DATA-08")]
    public async Task Pick_confirmations_and_shipments_are_append_only_and_picks_never_exceed_their_task()
    {
        var granted = await ListAsync("""
            SELECT t || ':' || p FROM unnest(ARRAY['pick_confirmation','shipment']) t, unnest(ARRAY['UPDATE','DELETE','TRUNCATE']) p
            WHERE has_table_privilege('ti_app', 'ti.' || t, p)
            UNION ALL
            SELECT t || ':DELETE' FROM unnest(ARRAY['outbound_order','reservation','pick','shipment_line']) t WHERE has_table_privilege('ti_app', 'ti.' || t, 'DELETE')
            """);
        Assert.Empty(granted);
        foreach (var table in new[] { "pick_confirmation", "shipment" })
            Assert.Equal(2L, (long)(await Scalar(
                "SELECT count(*) FROM pg_trigger WHERE tgrelid = ('ti.' || @t)::regclass AND tgname IN (@t || '_append_only', @t || '_no_truncate')", ("t", table)))!);

        var w = await WorldAsync("out-pick");
        var (_, line) = await OrderAsync(w, requested: 5);
        var bucket = await BucketAsync(w, w.General, 10);
        var reservation = (Guid)(await Scalar("""
            INSERT INTO ti.reservation (tenant_id, order_line_id, product_id, bucket_id, qty, expires_at, created_by)
            VALUES (@t, @l, @p, @b, 5, now() + interval '24 hours', 'test') RETURNING id
            """, ("t", w.Tenant), ("l", line), ("p", w.General), ("b", bucket)))!;
        var pickId = (Guid)(await Scalar("""
            INSERT INTO ti.pick (tenant_id, reservation_id, warehouse_id, source_bin_id, product_id, task_qty, created_by)
            VALUES (@t, @r, @w, @bin, @p, 5, 'test') RETURNING id
            """, ("t", w.Tenant), ("r", reservation), ("w", w.Warehouse), ("bin", w.Storage), ("p", w.General)))!;
        await Expect(Check, "UPDATE ti.pick SET picked_qty = 6 WHERE id = @id", ("id", pickId));
        await Expect(PostgresErrorCodes.UniqueViolation, """
            INSERT INTO ti.pick (tenant_id, reservation_id, warehouse_id, source_bin_id, product_id, task_qty, created_by)
            VALUES (@t, @r, @w, @bin, @p, 5, 'test')
            """, ("t", w.Tenant), ("r", reservation), ("w", w.Warehouse), ("bin", w.Storage), ("p", w.General)); // one task per reservation segment
    }

    // ---------------------------------------------------------------------------------------------------------------

    private sealed record World(Guid Tenant, Guid Warehouse, Guid Storage, Guid Staging, Guid General, Guid Lotted, Guid Customer);

    private async Task<World> WorldAsync(string label)
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, label);
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        async Task<Guid> Create(string path, object body) =>
            Guid.Parse((string)(await (await admin.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!);
        var warehouse = await Create("/api/v1/warehouses", new { code = "W1", name = "Main", timeZone = "UTC" });
        var zone = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "Z1", name = "Ambient" });
        var storage = await Create("/api/v1/bins", new { zoneId = zone, code = "A-01", kind = "Storage" });
        var staging = await Create("/api/v1/bins", new { zoneId = zone, code = "DSP", kind = "Staging" });
        var unit = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        object Product(string sku, string tracking) => new { sku, name = sku, handlingProfile = "General", trackingMode = tracking, baseUomId = unit, basePrecision = 0 };
        var general = await Create("/api/v1/products", Product("GEN", "Quantity"));
        var lotted = await Create("/api/v1/products", Product("LOT", "Lot"));
        var customer = await Create("/api/v1/partners", new { code = "CUS", name = "Customer", type = "Customer" });
        return new World(tenant.TenantId, warehouse, storage, staging, general, lotted, customer);
    }

    private async Task<(Guid Order, Guid Line)> OrderAsync(World w, decimal requested)
    {
        var order = (Guid)(await Scalar("""
            INSERT INTO ti.outbound_order (tenant_id, customer_id, warehouse_id, reference, planned_dispatch_date, created_by)
            VALUES (@t, @c, @w, @r, current_date + 1, 'test') RETURNING id
            """, ("t", w.Tenant), ("c", w.Customer), ("w", w.Warehouse), ("r", $"SO-{Guid.NewGuid():N}"[..20])))!;
        return (order, await OrderLineAsync(w, order, 1, requested));
    }

    private async Task<Guid> OrderLineAsync(World w, Guid order, int lineNumber, decimal requested) => (Guid)(await Scalar("""
        INSERT INTO ti.outbound_order_line (tenant_id, order_id, line_number, product_id, requested_base_qty, created_by)
        VALUES (@t, @o, @n, @p, @q, 'test') RETURNING id
        """, ("t", w.Tenant), ("o", order), ("n", lineNumber), ("p", w.General), ("q", requested)))!;

    private async Task<Guid> BucketAsync(World w, Guid product, decimal onHand, bool lot = false, bool staging = false)
    {
        object lotId = DBNull.Value;
        if (lot)
            lotId = (Guid)(await Scalar("""
                INSERT INTO ti.lot (tenant_id, product_id, handling_profile, lot_number, created_by) VALUES (@t, @p, 'General', @n, 'test') RETURNING id
                """, ("t", w.Tenant), ("p", product), ("n", $"L{Guid.NewGuid():N}"[..12])))!;
        return (Guid)(await Scalar("""
            INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, lot_id, quality_status, received_at, on_hand_qty, created_by)
            VALUES (@t, @w, @b, @p, @l, 'Available', now(), @q, 'test') RETURNING id
            """, ("t", w.Tenant), ("w", w.Warehouse), ("b", staging ? w.Staging : w.Storage), ("p", product), ("l", lotId), ("q", onHand)))!;
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
