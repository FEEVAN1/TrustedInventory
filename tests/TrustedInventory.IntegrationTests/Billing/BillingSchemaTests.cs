using Npgsql;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Billing;

/// <summary>Migration 0017 (DEC-062): control-plane-only tables and billing task invariants.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class BillingSchemaTests(ApiFixture fx)
{
    [Theory]
    [Trait("Requirement", "SAS-004")]
    [InlineData("cp.billing_task")]
    [InlineData("cp.billing_sync_state")]
    [InlineData("cp.catalog_event")]
    [InlineData("cp.webhook_inbox")]
    public async Task Tenant_requests_cannot_read_operator_billing_tables(string table)
    {
        await using var conn = new NpgsqlConnection(fx.AppConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table}", conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    [Trait("Requirement", "SAS-004")]
    public async Task One_cause_opens_one_task_and_a_resolution_needs_who_when_and_how()
    {
        await using var conn = new NpgsqlConnection(fx.ControlConnection);
        await conn.OpenAsync();
        var key = "dispute:" + Guid.NewGuid().ToString("N");
        const string insert = """
            INSERT INTO cp.billing_task (kind, provider, dedupe_key, detail) VALUES ('Dispute', 'simulator', @k, 'Synthetic dispute')
            ON CONFLICT (dedupe_key) DO NOTHING
            """;
        Assert.Equal(1, await ExecAsync(conn, insert, ("k", key)));
        Assert.Equal(0, await ExecAsync(conn, insert, ("k", key)));

        var half = Assert.ThrowsAsync<PostgresException>(() => ExecAsync(conn,
            "UPDATE cp.billing_task SET status = 'Resolved', resolved_at = now() WHERE dedupe_key = @k", ("k", key)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await half).SqlState);
        Assert.Equal(1, await ExecAsync(conn, """
            UPDATE cp.billing_task SET status = 'Resolved', resolved_at = now(), resolved_by = 'user-platform-billing', resolution = 'Reviewed'
            WHERE dedupe_key = @k
            """, ("k", key)));
        var delete = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(conn, "DELETE FROM cp.billing_task WHERE dedupe_key = @k", ("k", key)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, delete.SqlState);
    }

    [Fact]
    [Trait("Requirement", "ENT-001")]
    [Trait("Requirement", "BIL-001")]
    public async Task A_referenced_price_can_be_linked_to_the_provider_once_and_never_changed()
    {
        await using var conn = new NpgsqlConnection(fx.ControlConnection);
        await conn.OpenAsync();
        // Seeded tenant A's subscription references its price, which has no provider reference yet.
        await using var find = new NpgsqlCommand("SELECT price_version_id FROM cp.subscription WHERE tenant_id = @t", conn);
        find.Parameters.AddWithValue("t", fx.Seed.A.TenantId);
        var price = (Guid)(await find.ExecuteScalarAsync())!;
        foreach (var change in new[] { "minor_unit_amount = minor_unit_amount + 1", "billing_interval = 'Year'", "active_from = active_from - interval '1 day'" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(conn, $"UPDATE cp.price_version SET {change} WHERE id = @p", ("p", price)));
            Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, ex.SqlState);
        }
        Assert.Equal(1, await ExecAsync(conn,
            "UPDATE cp.price_version SET provider = 'simulator', provider_price_ref = @r WHERE id = @p AND provider_price_ref IS NULL",
            ("p", price), ("r", "sim_price_" + price.ToString("N"))));
        var relink = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(conn,
            "UPDATE cp.price_version SET provider_price_ref = 'sim_price_other' WHERE id = @p", ("p", price)));
        Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, relink.SqlState);
    }

    [Fact]
    [Trait("Requirement", "SAS-004")]
    [Trait("Requirement", "BIL-007")]
    public async Task A_tenant_relays_only_its_own_billing_notices_and_only_marks_them_relayed_once()
    {
        await using (var control = new NpgsqlConnection(fx.ControlConnection))
        {
            await control.OpenAsync();
            foreach (var tenant in new[] { fx.Seed.A.TenantId, fx.Seed.B.TenantId })
                await ExecAsync(control, """
                    INSERT INTO cp.billing_notice (tenant_id, kind, dedupe_key, detail, created_at) VALUES (@t, 'PaymentFailed', @k, '{}', now())
                    """, ("t", tenant), ("k", "schema-test:" + Guid.NewGuid().ToString("N")));
            // The control plane writes notices but still cannot touch any tenant's outbox (SAS-004).
            var outbox = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(control,
                "INSERT INTO ti.outbox_event (tenant_id, event_id, event_type, payload) VALUES (@t, gen_random_uuid(), 'X', '{}')", ("t", fx.Seed.A.TenantId)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, outbox.SqlState);
        }

        await using var app = new NpgsqlConnection(fx.AppConnection);
        await app.OpenAsync();
        await using var tx = await app.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", app, tx))
        {
            set.Parameters.AddWithValue("t", fx.Seed.A.TenantId.ToString());
            await set.ExecuteScalarAsync();
        }
        await using (var foreign = new NpgsqlCommand("SELECT count(*) FROM cp.billing_notice WHERE tenant_id <> @t", app, tx))
        {
            foreign.Parameters.AddWithValue("t", fx.Seed.A.TenantId);
            Assert.Equal(0L, (long)(await foreign.ExecuteScalarAsync())!);
        }
        Assert.True(await ExecAsync(app, tx, "UPDATE cp.billing_notice SET relayed_at = now() WHERE relayed_at IS NULL") >= 1);
        await using (var savepoint = new NpgsqlCommand("SAVEPOINT s", app, tx))
            await savepoint.ExecuteNonQueryAsync();
        var again = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(app, tx, "UPDATE cp.billing_notice SET relayed_at = now() + interval '1 hour'"));
        Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, again.SqlState);
        await using (var rollback = new NpgsqlCommand("ROLLBACK TO SAVEPOINT s", app, tx))
            await rollback.ExecuteNonQueryAsync();
        var kind = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(app, tx, "UPDATE cp.billing_notice SET kind = 'PaymentRecovered'"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, kind.SqlState);
    }

    private static async Task<int> ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> ExecAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteNonQueryAsync();
    }
}
