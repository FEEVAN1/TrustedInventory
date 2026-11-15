using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using TrustedInventory.Application.Audit;
using TrustedInventory.Application.Jobs;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Infrastructure.Jobs;

/// <summary>
/// Signs each tenant's audit chain head once per UTC day (AUD-003) and stores the checkpoint both in
/// ti.audit_checkpoint and in separately administered retention storage. Re-running on the same day is a no-op.
/// </summary>
public sealed partial class AuditCheckpointJob(
    TenantJobRunner runner, IRetentionStore store, CheckpointSigningOptions signing, ILogger<AuditCheckpointJob> log) : IJob
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        // Every mode but Deleted, suspended and closed tenants included: their audit evidence stays protected (DEC-043).
        foreach (var (tenantId, status) in await runner.TenantsAsync(BackgroundJob.AuditCheckpoint, ct))
        {
            var created = await runner.UnitOfWorkFor(tenantId, status).WriteAsync((s, c) => CheckpointAsync(s, tenantId, c), ct);
            if (created)
                LogCreated(log, tenantId);
        }
    }

    private async Task<bool> CheckpointAsync(Application.Persistence.ITenantSession s, Guid tenantId, CancellationToken ct)
    {
        var (conn, tx) = TenantJobRunner.Raw(s);
        var date = DateOnly.FromDateTime(s.Now.UtcDateTime);

        await using (var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM ti.audit_checkpoint WHERE checkpoint_date = @d)", conn, tx))
        {
            exists.Parameters.AddWithValue("d", date);
            if (await exists.ExecuteScalarAsync(ct) is true)
                return false;
        }

        long sequence;
        byte[] head;
        await using (var stream = new NpgsqlCommand("SELECT last_sequence, last_hash FROM ti.audit_stream", conn, tx))
        await using (var reader = await stream.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException($"Tenant {tenantId} has no audit stream.");
            sequence = reader.GetInt64(0);
            head = (byte[])reader[1];
        }

        // A deleted tenant keeps its audit chain as deletion evidence (DEC-072): it is checkpointed once more after the deletion
        // events, then only if the retained chain ever changes.
        await using (var covered = new NpgsqlCommand("""
            SELECT (SELECT lifecycle::text FROM ti.tenant WHERE id = @t) = 'Deleted'
               AND EXISTS (SELECT 1 FROM ti.audit_checkpoint WHERE sequence = @seq)
            """, conn, tx))
        {
            covered.Parameters.AddWithValue("t", tenantId);
            covered.Parameters.AddWithValue("seq", sequence);
            if (await covered.ExecuteScalarAsync(ct) is true)
                return false;
        }

        var signature = AuditCheckpointSigner.Sign(signing.Key, tenantId, date, sequence, head);
        var record = new AuditCheckpointRecord(tenantId, date, sequence, Convert.ToHexStringLower(head),
            Convert.ToHexStringLower(signature), signing.KeyId);
        var reference = await store.WriteCheckpointAsync(record, ct);

        await using var insert = new NpgsqlCommand("""
            INSERT INTO ti.audit_checkpoint (tenant_id, checkpoint_date, sequence, event_hash, signature, signing_key_id, external_reference)
            VALUES (@t, @d, @seq, @hash, @sig, @kid, @ref)
            ON CONFLICT (tenant_id, checkpoint_date) DO NOTHING
            """, conn, tx);
        insert.Parameters.AddWithValue("t", tenantId);
        insert.Parameters.AddWithValue("d", date);
        insert.Parameters.AddWithValue("seq", sequence);
        insert.Parameters.AddWithValue("hash", head);
        insert.Parameters.AddWithValue("sig", signature);
        insert.Parameters.AddWithValue("kid", signing.KeyId);
        insert.Parameters.AddWithValue("ref", reference);
        return await insert.ExecuteNonQueryAsync(ct) == 1;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit checkpoint created for tenant {TenantId}")]
    private static partial void LogCreated(ILogger logger, Guid tenantId);
}

/// <summary>HMAC-SHA256 over the canonical JSON {tenantId, date, sequence, eventHash} (AUD-003).</summary>
public static class AuditCheckpointSigner
{
    public static string Payload(Guid tenantId, DateOnly date, long sequence, byte[] eventHash) =>
        CanonicalJson.Serialize(new JsonObject
        {
            ["tenantId"] = tenantId.ToString(),
            ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["sequence"] = sequence,
            ["eventHash"] = Convert.ToHexStringLower(eventHash),
        });

    public static byte[] Sign(byte[] key, Guid tenantId, DateOnly date, long sequence, byte[] eventHash) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Payload(tenantId, date, sequence, eventHash)));

    public static bool Verify(byte[] key, Guid tenantId, DateOnly date, long sequence, byte[] eventHash, byte[] signature) =>
        CryptographicOperations.FixedTimeEquals(Sign(key, tenantId, date, sequence, eventHash), signature);
}
