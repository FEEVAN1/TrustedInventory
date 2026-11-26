using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Jobs;
using TrustedInventory.Application.Operations;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Saas;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Infrastructure.Audit;
using TrustedInventory.Infrastructure.Billing;
using TrustedInventory.Infrastructure.Jobs;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Saas;

/// <summary>
/// Approved deletion (LFC-004, DEC-072). A closed workspace is deleted only when the owner asked for it, platform operations
/// approved it, the export window has ended, no retention hold is set and no safety obligation remains (rechecked here). Steps,
/// each idempotent and recorded in the tenant's retained audit chain: (1) DeletionPending; (2) cp.purge_tenant() removes the live
/// rows, memberships and grants in the database, which checks the approval again; (3) the tenant's objects are removed from
/// storage; (4) Deleted, with the backup expiry date from the approved schedule. A record left DeletionPending by a crash resumes
/// at step 2. Nothing here deletes because a trial expired (LFC-002).
/// </summary>
public sealed partial class TenantDeletionJob(ControlDataSource control, TenantJobRunner runner, IObjectStore store, LifecycleOptions options,
    TimeProvider clock, ILogger<TenantDeletionJob> log) : IJob
{
    public const string Subject = "system:tenant-deletion";

    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var (tenantId, recordId, status) in await DueAsync(ct))
        {
            try
            {
                await DeleteAsync(tenantId, recordId, status, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(log, e, tenantId);
            }
        }
    }

    /// <summary>Runs the deletion of one tenant now (used by the job and by tests with a controlled clock).</summary>
    public async Task<ClosureStatus> DeleteAsync(Guid tenantId, Guid recordId, ClosureStatus status, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (status == ClosureStatus.ExportWindow)
        {
            // Safety obligations are rechecked in the tenant's own transaction: stock may have changed since closure.
            var blockers = await runner.UnitOfWorkFor(tenantId, TenantStatus.Active).ReadAsync(ClosureBlockers.ComputeAsync, ct);
            if (blockers.Count > 0)
            {
                await BlockAsync(tenantId, recordId, blockers, now, ct);
                return ClosureStatus.Blocked;
            }
            if (!await StartAsync(tenantId, recordId, now, ct))
                return status;
        }

        string counts;
        await using (var cmd = control.Source.CreateCommand("SELECT cp.purge_tenant(@t)::text"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            counts = (string)(await cmd.ExecuteScalarAsync(ct))!;
        }
        var objects = await store.DeleteTenantAsync(tenantId, ct);
        await CompleteAsync(tenantId, recordId, counts, objects, clock.GetUtcNow(), ct);
        return ClosureStatus.Deleted;
    }

    private async Task<List<(Guid TenantId, Guid RecordId, ClosureStatus Status)>> DueAsync(CancellationToken ct)
    {
        await using var cmd = control.Source.CreateCommand("""
            SELECT d.tenant_id, d.id, d.status::text FROM cp.deletion_record d JOIN ti.tenant t ON t.id = d.tenant_id
            WHERE (d.status = 'ExportWindow' AND d.deletion_requested AND d.approved_at IS NOT NULL AND d.export_window_ends_at <= @now
                   AND t.lifecycle = 'Archived' AND NOT t.retention_hold)
               OR (d.status = 'DeletionPending' AND t.lifecycle = 'DeletionPending' AND NOT t.retention_hold)
            ORDER BY d.export_window_ends_at, d.id
            """);
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
        var due = new List<(Guid, Guid, ClosureStatus)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            due.Add((r.GetGuid(0), r.GetGuid(1), Enum.Parse<ClosureStatus>(r.GetString(2))));
        return due;
    }

    private async Task<bool> StartAsync(Guid tenantId, Guid recordId, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var version = await Sql.ScalarAsync<int?>(conn, tx, """
            SELECT version FROM ti.tenant WHERE id = @t AND lifecycle = 'Archived' AND NOT retention_hold FOR UPDATE
            """, ct, ("t", tenantId));
        var started = version is not null && await Sql.ExecAsync(conn, tx, """
            UPDATE cp.deletion_record SET status = 'DeletionPending', updated_at = @now, version = version + 1
            WHERE id = @id AND status = 'ExportWindow' AND approved_at IS NOT NULL AND export_window_ends_at <= @now
            """, ct, ("now", now), ("id", recordId)) == 1;
        if (!started)
        {
            await tx.RollbackAsync(ct);
            return false;
        }
        await Sql.ExecAsync(conn, tx, """
            UPDATE ti.tenant SET lifecycle = 'DeletionPending', deletion_requested_at = coalesce(deletion_requested_at, @now),
                   updated_at = @now, version = version + 1
            WHERE id = @t
            """, ct, ("now", now), ("t", tenantId));
        await AuditAppender.AppendAsync(conn, tx, tenantId, Actor, now,
        [
            new AuditEntry("TenantDeletionStarted", "Tenant", tenantId, version + 1, "Approved deletion after the export window",
                new { lifecycle = nameof(TenantLifecycle.Archived) }, new { lifecycle = nameof(TenantLifecycle.DeletionPending) }),
        ], ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private async Task BlockAsync(Guid tenantId, Guid recordId, IReadOnlyList<ClosureBlocker> blockers, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var invoices = await Sql.ScalarAsync<int?>(conn, tx, "SELECT outstanding_invoice_count FROM cp.deletion_record WHERE id = @id FOR UPDATE",
            ct, ("id", recordId)) ?? 0;
        await Sql.ExecAsync(conn, tx, """
            UPDATE cp.deletion_record SET status = 'Blocked', hold_results = @holds::jsonb, updated_at = @now, version = version + 1
            WHERE id = @id AND status = 'ExportWindow'
            """, ct, ("holds", ClosureStore.HoldResults(blockers, invoices, retentionHold: null)), ("now", now), ("id", recordId));
        await AuditAppender.AppendAsync(conn, tx, tenantId, Actor, now,
            [new AuditEntry("TenantDeletionBlocked", "Closure", recordId, null, "Safety obligations remain at the deletion date", null, new { blockers })], ct);
        await tx.CommitAsync(ct);
    }

    private async Task CompleteAsync(Guid tenantId, Guid recordId, string counts, int objects, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var version = await Sql.ScalarAsync<int?>(conn, tx, "SELECT version FROM ti.tenant WHERE id = @t FOR UPDATE", ct, ("t", tenantId));
        var backupExpiry = now.AddDays(options.BackupRetentionDays);
        await Sql.ExecAsync(conn, tx, """
            UPDATE ti.tenant SET lifecycle = 'Deleted', deleted_at = @now, updated_at = @now, version = version + 1
            WHERE id = @t AND lifecycle = 'DeletionPending'
            """, ct, ("now", now), ("t", tenantId));
        await Sql.ExecAsync(conn, tx, """
            UPDATE cp.deletion_record SET status = 'Deleted', completed_at = @now, backup_expiry_at = @expiry,
                   scope = @scope::jsonb, updated_at = @now, version = version + 1
            WHERE id = @id AND status = 'DeletionPending'
            """, ct, ("now", now), ("expiry", backupExpiry), ("scope", JsonSerializer.Serialize(new
            {
                rowsRemoved = JsonDocument.Parse(counts).RootElement,
                objectsRemoved = objects,
                retained = Retained,
            }, JsonDefaults.Options)), ("id", recordId));
        await AuditAppender.AppendAsync(conn, tx, tenantId, Actor, now,
        [
            new AuditEntry("TenantDeleted", "Tenant", tenantId, (version ?? 0) + 1, "Approved deletion completed",
                new { lifecycle = nameof(TenantLifecycle.DeletionPending) },
                new { lifecycle = nameof(TenantLifecycle.Deleted), objectsRemoved = objects, backupExpiryAt = backupExpiry }),
        ], ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>What deletion keeps, with restricted access and a documented reason (docs/tenant-offboarding.md).</summary>
    private static readonly string[] Retained =
        ["ti.tenant (deletion evidence)", "ti.audit_event", "ti.audit_checkpoint", "ti.audit_stream", "cp billing records (invoices, payments)", "cp.deletion_record"];

    private static readonly AuditActor Actor = new(Subject, ActorType.System, null, null);

    [LoggerMessage(Level = LogLevel.Error, Message = "Deletion of tenant {TenantId} failed; it resumes on the next run")]
    private static partial void LogFailed(ILogger logger, Exception error, Guid tenantId);
}
