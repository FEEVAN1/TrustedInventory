using Npgsql;

namespace TrustedInventory.Migrator;

/// <summary>
/// Applies db/migrations in order as ti_migrator acting as ti_owner (TEN-002). Each script runs in its own
/// transaction with its history row. An applied script whose content changed stops the run.
/// </summary>
public sealed class MigrationRunner(string migratorConnectionString, TextWriter log)
{
    // Arbitrary constant: serialises concurrent migrator instances during rolling deployments.
    private const long AdvisoryLockKey = 0x5449_4D49_4752; // "TIMIGR"

    public async Task<IReadOnlyList<string>> ApplyAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(migratorConnectionString);
        await conn.OpenAsync(ct);
        await ExecAsync(conn, "SET ROLE ti_owner", ct);
        await ExecAsync(conn, $"SELECT pg_advisory_lock({AdvisoryLockKey})", ct);
        try
        {
            await ExecAsync(conn, """
                CREATE TABLE IF NOT EXISTS ti.schema_migration (
                  id          text PRIMARY KEY,
                  checksum    text NOT NULL,
                  applied_at  timestamptz NOT NULL DEFAULT now()
                );
                GRANT SELECT ON ti.schema_migration TO ti_app;
                """, ct);

            var applied = await LoadAppliedAsync(conn, ct);
            var newlyApplied = new List<string>();
            foreach (var script in SqlScripts.Migrations)
            {
                if (applied.TryGetValue(script.Id, out var checksum))
                {
                    if (checksum != script.Checksum)
                        throw new InvalidOperationException(
                            $"Migration {script.Id} was changed after it was applied. Add a new migration instead.");
                    continue;
                }

                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var cmd = new NpgsqlCommand(script.Sql, conn, tx))
                    await cmd.ExecuteNonQueryAsync(ct);
                await using (var record = new NpgsqlCommand(
                    "INSERT INTO ti.schema_migration (id, checksum) VALUES (@id, @checksum)", conn, tx))
                {
                    record.Parameters.AddWithValue("id", script.Id);
                    record.Parameters.AddWithValue("checksum", script.Checksum);
                    await record.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                await log.WriteLineAsync($"applied {script.Id}");
                newlyApplied.Add(script.Id);
            }
            return newlyApplied;
        }
        finally
        {
            await ExecAsync(conn, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
        }
    }

    /// <summary>The newest migration id this build expects; the API readiness check compares against it.</summary>
    public static string LatestMigrationId => SqlScripts.Migrations[^1].Id;

    private static async Task<Dictionary<string, string>> LoadAppliedAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var applied = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand("SELECT id, checksum FROM ti.schema_migration", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            applied[reader.GetString(0)] = reader.GetString(1);
        return applied;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
