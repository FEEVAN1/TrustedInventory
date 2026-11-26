using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Jobs;
using TrustedInventory.Application.Operations;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Domain.Operations;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Infrastructure.Jobs;

/// <summary>
/// Export generation (AUD-004, NFR-EXPORTS, DEC-061): claims queued exports one at a time (SKIP LOCKED), writes the JSON
/// Lines file to the object store, and completes the job with an integrity manifest signed with the audit checkpoint
/// key. A failure marks the job Failed in its own transaction.
/// Fair scheduling (SOP-001, DEC-073): each pass takes at most one export per tenant, round-robin, so a tenant with a long queue
/// waits its turn instead of holding the worker; a run stops after <paramref name="maxPerRun"/> exports.
/// </summary>
public sealed partial class ExportGenerationJob(TenantJobRunner runner, IObjectStore store, CheckpointSigningOptions signing, ILogger<ExportGenerationJob> log,
    int maxPerRun = 50) : IJob
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var pending = (await runner.TenantsAsync(BackgroundJob.ExportGeneration, ct)).ToList();
        var done = 0;
        while (pending.Count > 0 && done < maxPerRun && !ct.IsCancellationRequested)
        {
            var next = new List<(Guid, Domain.Organization.TenantStatus)>();
            foreach (var (tenantId, status) in pending)
            {
                if (done >= maxPerRun || ct.IsCancellationRequested)
                    break;
                var uow = runner.UnitOfWorkFor(tenantId, status);
                Guid? handled;
                try
                {
                    handled = await uow.WriteAsync((s, c) => GenerateNextAsync(s, c), ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    handled = await uow.WriteAsync((s, c) => FailNextAsync(s, e.Message, c), ct);
                    LogFailed(log, e, handled, tenantId);
                }
                if (handled is null)
                    continue; // nothing queued for this tenant: it leaves the rotation
                done++;
                next.Add((tenantId, status));
            }
            pending = next;
        }
    }

    private async Task<Guid?> GenerateNextAsync(ITenantSession s, CancellationToken ct)
    {
        var job = await ClaimAsync(s, ct);
        if (job is null)
            return null;
        job.Start(s.Now);
        ExportContent.Result result;
        var fileCount = 1;
        if (job.Type == ExportType.TenantArchive)
        {
            var archive = await TenantArchive.BuildAsync(s, job, store, ct);
            result = new ExportContent.Result(archive.Content, 0, archive.ToSequence, archive.Rows);
            fileCount = archive.FileCount;
        }
        else
        {
            result = await ExportContent.BuildAsync(s, job, ct);
        }
        var digest = Convert.ToHexStringLower(SHA256.HashData(result.Content));
        var signed = $"{digest}|{Convert.ToHexStringLower(job.FilterDigest)}|{result.FromSequence}|{result.ToSequence}";
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(signing.Key, Encoding.UTF8.GetBytes(signed)));
        var manifest = new ExportManifest(digest, fileCount, $"hmac-sha256:{signing.KeyId}:{signature}");
        job.Complete(result.FromSequence, result.ToSequence, JsonSerializer.Serialize(manifest, JsonDefaults.Options), s.Now);
        await store.PutAsync(job.ObjectKey!, result.Content, ct);
        s.Audit(new AuditEntry("ExportCompleted", "ExportJob", job.Id, job.Version, null, null,
            new { exportId = job.Id, rows = result.Rows, manifest, streamRange = new { from = result.FromSequence, to = result.ToSequence } }));
        await s.Db.SaveChangesAsync(ct);
        return job.Id;
    }

    private static async Task<Guid?> FailNextAsync(ITenantSession s, string reason, CancellationToken ct)
    {
        var job = await ClaimAsync(s, ct);
        if (job is null)
            return null;
        job.Fail(string.IsNullOrWhiteSpace(reason) ? "Export generation failed." : reason, s.Now);
        s.Audit(new AuditEntry("ExportFailed", "ExportJob", job.Id, job.Version, job.FailureReason, null, new { exportId = job.Id }));
        await s.Db.SaveChangesAsync(ct);
        return job.Id;
    }

    private static async Task<ExportJob?> ClaimAsync(ITenantSession s, CancellationToken ct)
    {
        var ids = await s.Db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM ti.export_job WHERE status = 'Queued' ORDER BY created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        return ids.Count == 0 ? null : await s.Db.Set<ExportJob>().SingleAsync(j => j.Id == ids[0], ct);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Export {ExportId} failed in tenant {TenantId}")]
    private static partial void LogFailed(ILogger logger, Exception error, Guid? exportId, Guid tenantId);
}
