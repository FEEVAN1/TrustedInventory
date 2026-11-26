using System.Net;
using Npgsql;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Ledger;

/// <summary>A reversal never exceeds the unreversed quantity of the movement it reverses (section 09, STK-PRECISION, DEC-101).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ReversalGuardTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "STK-PRECISION")]
    [Trait("Requirement", "STK-004")]
    public async Task Reversals_stop_at_the_unreversed_quantity_and_are_never_reversed_themselves()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "reversal");
        await TenantProvisioning.GrantSelfAsync(fx, tenant, "WarehouseManager");
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        async Task<Guid> Create(string path, object body) =>
            Guid.Parse((string)(await (await admin.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!);
        var warehouse = await Create("/api/v1/warehouses", new { code = "W1", name = "Main", timeZone = "UTC" });
        var zone = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "Z1", name = "Ambient" });
        var bin = await Create("/api/v1/bins", new { zoneId = zone, code = "B1", kind = "Storage" });
        var unit = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        var product = await Create("/api/v1/products", new { sku = "REV", name = "Reversed", handlingProfile = "General", trackingMode = "Quantity", baseUomId = unit, basePrecision = 0 });
        var other = await Create("/api/v1/products", new { sku = "OTHER", name = "Other", handlingProfile = "General", trackingMode = "Quantity", baseUomId = unit, basePrecision = 0 });

        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        async Task<Guid> Bucket(Guid p) => (Guid)(await Scalar(conn, """
            INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, quality_status, received_at, on_hand_qty, created_by)
            VALUES (@t, @w, @b, @p, 'Available', now(), 0, 'test') RETURNING id
            """, ("t", tenant.TenantId), ("w", warehouse), ("b", bin), ("p", p)))!;
        var bucket = await Bucket(product);
        var otherBucket = await Bucket(other);
        async Task<Guid> Movement(string type, Guid p, Guid? source, Guid? destination, decimal qty, Guid? reversalOf) => (Guid)(await Scalar(conn, """
            INSERT INTO ti.stock_movement (tenant_id, movement_type, operation_key, product_id, source_bucket_id, destination_bucket_id, base_qty,
                                           document_type, document_id, reversal_of_id, actor_subject, occurred_at)
            VALUES (@t, @type::ti.movement_type, 'test:' || gen_random_uuid(), @p, @s, @d, @q, 'Receipt', gen_random_uuid(), @r, 'test', now())
            RETURNING id
            """, ("t", tenant.TenantId), ("type", type), ("p", p), ("s", (object?)source ?? DBNull.Value), ("d", (object?)destination ?? DBNull.Value),
            ("q", qty), ("r", (object?)reversalOf ?? DBNull.Value)))!;

        var receipt = await Movement("Receipt", product, null, bucket, 5m, null);
        var first = await Movement("Reversal", product, bucket, null, 3m, receipt);
        await Movement("Reversal", product, bucket, null, 2m, receipt);                              // exactly the rest
        var over = await Assert.ThrowsAsync<PostgresException>(() => Movement("Reversal", product, bucket, null, 0.000001m, receipt));
        Assert.Equal(PostgresErrorCodes.CheckViolation, over.SqlState);
        Assert.Contains("unreversed quantity", over.MessageText, StringComparison.Ordinal);

        var second = await Movement("Receipt", product, null, bucket, 4m, null);
        Assert.Contains("cannot itself be reversed", (await Assert.ThrowsAsync<PostgresException>(() => Movement("Reversal", product, null, bucket, 1m, first))).MessageText,
            StringComparison.Ordinal);
        Assert.Contains("product of the movement", (await Assert.ThrowsAsync<PostgresException>(() => Movement("Reversal", other, otherBucket, null, 1m, second))).MessageText,
            StringComparison.Ordinal);
        Assert.Contains("unreversed quantity", (await Assert.ThrowsAsync<PostgresException>(() => Movement("Reversal", product, bucket, null, 4.5m, second))).MessageText,
            StringComparison.Ordinal);
    }

    private static async Task<object?> Scalar(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteScalarAsync();
    }
}
