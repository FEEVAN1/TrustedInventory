using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using TrustedInventory.Application.Audit;
using TrustedInventory.Application.Jobs;
using TrustedInventory.Domain.Alerts;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Infrastructure.Jobs;

/// <summary>
/// Independent verification of each tenant's audit chain against its external checkpoints (AUD-003, AT-AUD-01, DEC-093). For every
/// checkpoint kept in the separately administered retention store it checks the signature with the checkpoint key, that the event at
/// the signed sequence still has the signed hash, and that the database's own checkpoint row agrees; a database checkpoint with no
/// external copy is also a failure. Any failure opens one High <c>AuditChainFailure</c> alert. Because each event hash covers the
/// previous one, rewriting, deleting or reordering any event at or before a checkpoint changes the hash at that checkpoint, which the
/// signature makes impossible to forge without the key.
/// </summary>
public sealed partial class AuditCheckpointVerificationJob(
    TenantJobRunner runner, IRetentionStore store, CheckpointSigningOptions signing, IAuditSupport support,
    ILogger<AuditCheckpointVerificationJob> log) : IJob
{
    public const string DedupeKey = "audit-external-checkpoint";

    private static readonly byte[] Genesis = new byte[32];

    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var (tenantId, status) in await runner.TenantsAsync(BackgroundJob.AuditCheckpoint, ct))
            await VerifyOneAsync(tenantId, status, ct);
    }

    /// <summary>Verifies one tenant; returns how many problems were found (each open problem keeps one alert open).</summary>
    public async Task<int> VerifyTenantAsync(Guid tenantId, CancellationToken ct)
    {
        foreach (var (id, status) in await runner.TenantsAsync(BackgroundJob.AuditCheckpoint, ct))
            if (id == tenantId)
                return await VerifyOneAsync(id, status, ct);
        return 0;
    }

    private async Task<int> VerifyOneAsync(Guid tenantId, Domain.Organization.TenantStatus status, CancellationToken ct)
    {
        var external = await store.ReadCheckpointsAsync(tenantId, ct);
        var failures = await runner.UnitOfWorkFor(tenantId, status).WriteAsync((s, c) => VerifyAsync(s, tenantId, external, c), ct);
        if (failures > 0)
            LogFailed(log, tenantId, failures);
        return failures;
    }

    private async Task<int> VerifyAsync(Application.Persistence.ITenantSession s, Guid tenantId, IReadOnlyList<AuditCheckpointRecord> external,
        CancellationToken ct)
    {
        var (conn, tx) = TenantJobRunner.Raw(s);
        var stored = new Dictionary<DateOnly, (long Sequence, byte[] Hash, byte[] Signature)>();
        await using (var rows = new NpgsqlCommand("SELECT checkpoint_date, sequence, event_hash, signature FROM ti.audit_checkpoint", conn, tx))
        await using (var reader = await rows.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                stored[reader.GetFieldValue<DateOnly>(0)] = (reader.GetInt64(1), (byte[])reader[2], (byte[])reader[3]);
        }

        var problems = new JsonArray();
        void Problem(DateOnly date, long sequence, string code) =>
            problems.Add(new JsonObject { ["date"] = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), ["sequence"] = sequence, ["problem"] = code });

        foreach (var x in external.OrderBy(x => x.Date))
        {
            byte[] hash, signature;
            try
            {
                hash = Convert.FromHexString(x.EventHashHex);
                signature = Convert.FromHexString(x.SignatureHex);
            }
            catch (FormatException)
            {
                Problem(x.Date, x.Sequence, "Unreadable");
                continue;
            }
            if (x.TenantId != tenantId)
                Problem(x.Date, x.Sequence, "ForeignCheckpoint");
            else if (x.SigningKeyId != signing.KeyId)
                Problem(x.Date, x.Sequence, "UnknownSigningKey");
            else if (!AuditCheckpointSigner.Verify(signing.Key, tenantId, x.Date, x.Sequence, hash, signature))
                Problem(x.Date, x.Sequence, "SignatureInvalid");

            // A checkpoint of an empty chain covers sequence 0, whose hash is the stream's genesis value (32 zero bytes, migration 0001);
            // there is no event 0 to compare with (DEC-095).
            if (x.Sequence == 0)
            {
                if (!hash.AsSpan().SequenceEqual(Genesis))
                    Problem(x.Date, x.Sequence, "EventHashMismatch");
            }
            else
            await using (var at = new NpgsqlCommand("SELECT event_hash FROM ti.audit_event WHERE sequence = @seq", conn, tx))
            {
                at.Parameters.AddWithValue("seq", x.Sequence);
                var current = await at.ExecuteScalarAsync(ct) as byte[];
                if (current is null)
                    Problem(x.Date, x.Sequence, "EventMissing");
                else if (!current.AsSpan().SequenceEqual(hash))
                    Problem(x.Date, x.Sequence, "EventHashMismatch");
            }

            if (!stored.Remove(x.Date, out var row))
                Problem(x.Date, x.Sequence, "DatabaseCheckpointMissing");
            else if (row.Sequence != x.Sequence || !row.Hash.AsSpan().SequenceEqual(hash) || !row.Signature.AsSpan().SequenceEqual(signature))
                Problem(x.Date, x.Sequence, "DatabaseCheckpointMismatch");
        }
        foreach (var (date, row) in stored)
            Problem(date, row.Sequence, "NotExternal");

        if (problems.Count > 0)
            await support.RaiseAlertAsync(s, AlertType.AuditChainFailure, AlertSeverity.High, DedupeKey,
                new JsonObject { ["failure"] = "ExternalCheckpoint", ["problems"] = problems }, ct);
        return problems.Count;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit checkpoint verification found {Count} problem(s) for tenant {TenantId}")]
    private static partial void LogFailed(ILogger logger, Guid tenantId, int count);
}
