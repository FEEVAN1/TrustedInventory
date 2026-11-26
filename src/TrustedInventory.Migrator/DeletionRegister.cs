using System.Text.Json;
using Npgsql;

namespace TrustedInventory.Migrator;

/// <summary>One completed deletion, as replayed after a restore (docs/tenant-offboarding.md, LFC-004).</summary>
public sealed record DeletionRegisterEntry(Guid TenantId, Guid RecordId, string ApprovedBy, DateTimeOffset ApprovedAt, DateTimeOffset CompletedAt,
    DateTimeOffset BackupExpiryAt);

/// <summary>Result of verifying one replayed tenant: what is left that a deleted tenant must not have.</summary>
public sealed record DeletionVerification(Guid TenantId, string Lifecycle, long Memberships, long LiveRows)
{
    public bool Inaccessible => Lifecycle is "Deleted" or "absent" && Memberships == 0 && LiveRows == 0;
}

/// <summary>
/// The deletion register (LFC-004): exported from the live control plane, replayed against restored data before any tenant
/// access is opened, then verified. Runs on the ti_control role; the purge itself runs inside cp.replay_deletion().
/// </summary>
public static class DeletionRegister
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<IReadOnlyList<DeletionRegisterEntry>> ReadAsync(string controlConnection, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(controlConnection);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT tenant_id, id, approved_by, approved_at, completed_at, backup_expiry_at
            FROM cp.deletion_record WHERE status = 'Deleted' ORDER BY completed_at, id
            """, conn);
        var entries = new List<DeletionRegisterEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            entries.Add(new DeletionRegisterEntry(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3),
                r.GetFieldValue<DateTimeOffset>(4), r.GetFieldValue<DateTimeOffset>(5)));
        return entries;
    }

    public static async Task ExportAsync(string controlConnection, string path, TextWriter log, CancellationToken ct = default)
    {
        var entries = await ReadAsync(controlConnection, ct);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(entries, Json), ct);
        await log.WriteLineAsync($"deletion register: {entries.Count} entries written to {path}");
    }

    /// <summary>Replays every entry, then verifies each tenant. Returns false (and the caller keeps access closed) if any check fails.</summary>
    public static async Task<bool> ReplayAsync(string controlConnection, IReadOnlyList<DeletionRegisterEntry> entries, TextWriter log,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(controlConnection);
        await conn.OpenAsync(ct);
        foreach (var e in entries)
        {
            await using var cmd = new NpgsqlCommand("SELECT cp.replay_deletion(@t, @r, @by, @at, @done, @expiry)::text", conn);
            cmd.Parameters.AddWithValue("t", e.TenantId);
            cmd.Parameters.AddWithValue("r", e.RecordId);
            cmd.Parameters.AddWithValue("by", e.ApprovedBy);
            cmd.Parameters.AddWithValue("at", e.ApprovedAt);
            cmd.Parameters.AddWithValue("done", e.CompletedAt);
            cmd.Parameters.AddWithValue("expiry", e.BackupExpiryAt);
            var counts = (string?)await cmd.ExecuteScalarAsync(ct);
            await log.WriteLineAsync($"replayed deletion of tenant {e.TenantId}: {counts}");
        }
        var ok = true;
        foreach (var v in await VerifyAsync(conn, entries, ct))
        {
            ok &= v.Inaccessible;
            await log.WriteLineAsync($"verify tenant {v.TenantId}: lifecycle {v.Lifecycle}, memberships {v.Memberships}, live rows {v.LiveRows} -> " +
                (v.Inaccessible ? "inaccessible" : "STILL PRESENT"));
        }
        return ok;
    }

    public static async Task<IReadOnlyList<DeletionVerification>> VerifyAsync(NpgsqlConnection conn, IReadOnlyList<DeletionRegisterEntry> entries,
        CancellationToken ct = default)
    {
        var results = new List<DeletionVerification>();
        foreach (var e in entries)
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT coalesce((SELECT lifecycle::text FROM ti.tenant WHERE id = @t), 'absent'),
                       (SELECT count(*) FROM ti.membership WHERE tenant_id = @t) + (SELECT count(*) FROM ti.role_grant WHERE tenant_id = @t),
                       (SELECT count(*) FROM ti.invitation WHERE tenant_id = @t) + (SELECT count(*) FROM ti.usage_counter WHERE tenant_id = @t)
                """, conn);
            cmd.Parameters.AddWithValue("t", e.TenantId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            results.Add(new DeletionVerification(e.TenantId, r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        }
        return results;
    }

    public static IReadOnlyList<DeletionRegisterEntry> Load(string path) =>
        JsonSerializer.Deserialize<List<DeletionRegisterEntry>>(File.ReadAllText(path), Json)
        ?? throw new InvalidOperationException($"{path} is not a deletion register.");
}
