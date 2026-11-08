using System.Text.Json;
using Npgsql;

namespace TrustedInventory.Migrator;

/// <summary>
/// Loads the SYNTHETIC fixture plan catalog (docs/entitlement-catalog.json) into cp as ti_control, for
/// development stacks only. Fixture prices use the test currency XTS and are never approved for display or
/// charging (D09, DEC-040). Re-running skips plans that already exist; published plans are immutable (ENT-001).
/// </summary>
public static class CatalogSeed
{
    public static async Task ApplyAsync(string controlConnectionString, string catalogPath, TextWriter log, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(catalogPath, ct));
        if (!doc.RootElement.TryGetProperty("fixture", out var fixture) || !fixture.GetBoolean())
            throw new InvalidOperationException("Only a catalog marked \"fixture\": true can be seeded by this command.");

        await using var conn = new NpgsqlConnection(controlConnectionString);
        await conn.OpenAsync(ct);
        foreach (var plan in doc.RootElement.GetProperty("plans").EnumerateArray())
        {
            var code = plan.GetProperty("planCode").GetString()!;
            var version = plan.GetProperty("version").GetInt32();
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM cp.plan_version WHERE plan_code = @c AND version = @v)", conn, tx))
            {
                exists.Parameters.AddWithValue("c", code);
                exists.Parameters.AddWithValue("v", version);
                if ((bool)(await exists.ExecuteScalarAsync(ct))!)
                {
                    await log.WriteLineAsync($"plan {code} v{version} already present");
                    continue;
                }
            }

            Guid planId;
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO cp.plan_version (plan_code, version, display_name, features, quotas, is_public, created_by)
                VALUES (@c, @v, @n, @f, @q::jsonb, @pub, 'system:catalog-seed') RETURNING id
                """, conn, tx))
            {
                insert.Parameters.AddWithValue("c", code);
                insert.Parameters.AddWithValue("v", version);
                insert.Parameters.AddWithValue("n", plan.GetProperty("displayName").GetString()!);
                insert.Parameters.AddWithValue("f", plan.GetProperty("features").EnumerateArray().Select(f => f.GetString()!).ToArray());
                insert.Parameters.AddWithValue("q", plan.GetProperty("quotas").GetRawText());
                insert.Parameters.AddWithValue("pub", plan.GetProperty("public").GetBoolean());
                planId = (Guid)(await insert.ExecuteScalarAsync(ct))!;
            }

            foreach (var price in plan.GetProperty("prices").EnumerateArray())
            {
                await using var p = new NpgsqlCommand("""
                    INSERT INTO cp.price_version (plan_version_id, currency, minor_unit_amount, exponent, billing_interval, active_from, created_by)
                    VALUES (@plan, @cur, @amt, @exp, @int::cp.billing_interval, now(), 'system:catalog-seed')
                    """, conn, tx);
                p.Parameters.AddWithValue("plan", planId);
                p.Parameters.AddWithValue("cur", price.GetProperty("currency").GetString()!);
                p.Parameters.AddWithValue("amt", price.GetProperty("minorUnitAmount").GetInt64());
                p.Parameters.AddWithValue("exp", (short)price.GetProperty("exponent").GetInt32());
                p.Parameters.AddWithValue("int", price.GetProperty("interval").GetString()!);
                await p.ExecuteNonQueryAsync(ct);
            }

            // Publish last: a published plan version can no longer change (ENT-001).
            await using (var publish = new NpgsqlCommand("UPDATE cp.plan_version SET published_at = now() WHERE id = @id", conn, tx))
            {
                publish.Parameters.AddWithValue("id", planId);
                await publish.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            await log.WriteLineAsync($"seeded fixture plan {code} v{version}");
        }
    }
}
