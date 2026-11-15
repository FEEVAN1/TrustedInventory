using System.Data;
using System.Text.Json;
using Npgsql;
using TrustedInventory.Application.Billing;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Organization;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Saas;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Infrastructure.Audit;
using TrustedInventory.Infrastructure.Billing;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Saas;

/// <summary>
/// Closure records and platform lifecycle controls on the ti_control pool (DEC-070). Lock order: tenant row → deletion record →
/// audit stream (last). The tenant id comes from the validated caller or the platform route, never from a payload.
/// </summary>
public sealed class ClosureStore(ControlDataSource control) : IClosureStore
{
    private const string RecordColumns = """
        id, tenant_id, status::text, requested_at, requested_by, deletion_requested, export_window_ends_at, archive_export_id,
        outstanding_invoice_count, hold_results::text, idempotency_key
        """;

    public async Task<ClosureTenant?> LoadTenantAsync(Guid tenantId, CancellationToken ct)
    {
        await using var cmd = control.Source.CreateCommand("""
            SELECT t.id, t.slug, t.lifecycle::text, t.version, s.state::text, coalesce(s.cancel_at_period_end, false),
                   CASE s.state WHEN 'Trialing' THEN s.trial_end WHEN 'Cancelled' THEN NULL ELSE s.period_end END
            FROM ti.tenant t LEFT JOIN cp.subscription s ON s.tenant_id = t.id WHERE t.id = @t
            """);
        cmd.Parameters.AddWithValue("t", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;
        return new ClosureTenant(r.GetGuid(0), r.IsDBNull(1) ? "" : r.GetString(1), Enum.Parse<TenantLifecycle>(r.GetString(2)), r.GetInt32(3),
            r.IsDBNull(4) ? null : Enum.Parse<SubscriptionState>(r.GetString(4)), r.GetBoolean(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6));
    }

    public async Task<ClosureRow?> FindByKeyAsync(Guid tenantId, Guid idempotencyKey, CancellationToken ct)
    {
        await using var cmd = control.Source.CreateCommand($"SELECT {RecordColumns} FROM cp.deletion_record WHERE tenant_id = @t AND idempotency_key = @k");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("k", idempotencyKey);
        return await ReadOneAsync(cmd, ct);
    }

    public async Task<int> OutstandingInvoicesAsync(Guid tenantId, CancellationToken ct)
    {
        await using var cmd = control.Source.CreateCommand("SELECT count(*)::int FROM cp.invoice WHERE tenant_id = @t AND status = 'Open'");
        cmd.Parameters.AddWithValue("t", tenantId);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<ClosureRow> RecordBlockedAsync(BillingActor actor, ClosureCommand cmd, IReadOnlyList<ClosureBlocker> blockers, DateTimeOffset now,
        CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockTenantAsync(conn, tx, actor.TenantId, ct);
        var holdResults = HoldResults(blockers, cmd.OutstandingInvoices, retentionHold: null);
        var open = await OpenRecordAsync(conn, tx, actor.TenantId, ct);
        Guid id;
        if (open is null)
        {
            id = (await Sql.ScalarAsync<Guid?>(conn, tx, """
                INSERT INTO cp.deletion_record (tenant_id, status, requested_by, requested_at, reason, reauth_reference, deletion_requested, hold_results,
                                                idempotency_key, outstanding_invoice_count, created_at, updated_at)
                VALUES (@t, 'Blocked', @by, @now, @reason, @reauth, @deletion, @holds::jsonb, @key, @invoices, @now, @now) RETURNING id
                """, ct, ("t", actor.TenantId), ("by", actor.Subject), ("now", now), ("reason", cmd.Reason), ("reauth", cmd.ReauthReference),
                ("deletion", cmd.DeletionRequested), ("holds", holdResults), ("key", cmd.IdempotencyKey), ("invoices", cmd.OutstandingInvoices)))!.Value;
        }
        else if (open.Status == ClosureStatus.Blocked)
        {
            id = open.Id;
            await Sql.ExecAsync(conn, tx, """
                UPDATE cp.deletion_record SET hold_results = @holds::jsonb, idempotency_key = @key, requested_by = @by, requested_at = @now, reason = @reason,
                       reauth_reference = @reauth, deletion_requested = @deletion, outstanding_invoice_count = @invoices, updated_at = @now, version = version + 1
                WHERE id = @id
                """, ct, ("holds", holdResults), ("key", cmd.IdempotencyKey), ("by", actor.Subject), ("now", now), ("reason", cmd.Reason),
                ("reauth", cmd.ReauthReference), ("deletion", cmd.DeletionRequested), ("invoices", cmd.OutstandingInvoices), ("id", id));
        }
        else
        {
            throw AppException.InvalidState("A closure is already in progress for this workspace.");
        }
        await AuditAppender.AppendAsync(conn, tx, actor.TenantId, Actor(actor), now,
            [new AuditEntry("TenantClosureBlocked", "Closure", id, null, cmd.Reason, null,
                new { status = nameof(ClosureStatus.Blocked), blockers, outstandingInvoiceCount = cmd.OutstandingInvoices })], ct);
        var row = await ReadAsync(conn, tx, id, ct);
        await tx.CommitAsync(ct);
        return row;
    }

    public async Task<ClosureRow> ArchiveAsync(BillingActor actor, ClosureCommand cmd, int exportWindowDays, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var (lifecycle, version) = await LockTenantAsync(conn, tx, actor.TenantId, ct);
        DomainGuard.Run(() => ClosureRules.EnsureClosable(lifecycle));
        var serviceEnds = await Sql.ScalarAsync<DateTime?>(conn, tx, """
            SELECT CASE state WHEN 'Trialing' THEN trial_end WHEN 'Cancelled' THEN NULL ELSE period_end END FROM cp.subscription WHERE tenant_id = @t
            """, ct, ("t", actor.TenantId));
        var windowEnds = DomainGuard.Run(() => ClosureRules.ExportWindowEnds(now,
            serviceEnds is { } e ? new DateTimeOffset(DateTime.SpecifyKind(e, DateTimeKind.Utc)) : null, exportWindowDays));
        var holdResults = HoldResults([], cmd.OutstandingInvoices, retentionHold: null);

        var open = await OpenRecordAsync(conn, tx, actor.TenantId, ct);
        Guid id;
        if (open is null)
        {
            id = (await Sql.ScalarAsync<Guid?>(conn, tx, """
                INSERT INTO cp.deletion_record (tenant_id, status, requested_by, requested_at, reason, reauth_reference, deletion_requested,
                                                export_window_ends_at, hold_results, idempotency_key, outstanding_invoice_count, archive_export_id, created_at, updated_at)
                VALUES (@t, 'ExportWindow', @by, @now, @reason, @reauth, @deletion, @ends, @holds::jsonb, @key, @invoices, @export, @now, @now) RETURNING id
                """, ct, ("t", actor.TenantId), ("by", actor.Subject), ("now", now), ("reason", cmd.Reason), ("reauth", cmd.ReauthReference),
                ("deletion", cmd.DeletionRequested), ("ends", windowEnds), ("holds", holdResults), ("key", cmd.IdempotencyKey),
                ("invoices", cmd.OutstandingInvoices), ("export", (object?)cmd.ArchiveExportId ?? DBNull.Value)))!.Value;
        }
        else if (open.Status == ClosureStatus.Blocked)
        {
            id = open.Id;
            await Sql.ExecAsync(conn, tx, """
                UPDATE cp.deletion_record SET status = 'ExportWindow', export_window_ends_at = @ends, hold_results = @holds::jsonb, idempotency_key = @key,
                       requested_by = @by, requested_at = @now, reason = @reason, reauth_reference = @reauth, deletion_requested = @deletion,
                       outstanding_invoice_count = @invoices, archive_export_id = @export, updated_at = @now, version = version + 1
                WHERE id = @id
                """, ct, ("export", (object?)cmd.ArchiveExportId ?? DBNull.Value), ("ends", windowEnds), ("holds", holdResults), ("key", cmd.IdempotencyKey), ("by", actor.Subject), ("now", now),
                ("reason", cmd.Reason), ("reauth", cmd.ReauthReference), ("deletion", cmd.DeletionRequested), ("invoices", cmd.OutstandingInvoices),
                ("id", id));
        }
        else
        {
            throw AppException.InvalidState("A closure is already in progress for this workspace.");
        }

        await Sql.ExecAsync(conn, tx, """
            UPDATE ti.tenant SET lifecycle = 'Archived', archived_at = @now,
                   deletion_requested_at = CASE WHEN @deletion THEN @now ELSE NULL END, updated_at = @now, version = version + 1
            WHERE id = @t
            """, ct, ("now", now), ("deletion", cmd.DeletionRequested), ("t", actor.TenantId));
        await AuditAppender.AppendAsync(conn, tx, actor.TenantId, Actor(actor), now,
        [
            new AuditEntry("TenantClosureRequested", "Closure", id, null, cmd.Reason, null, new
            {
                status = nameof(ClosureStatus.ExportWindow),
                deletionRequested = cmd.DeletionRequested,
                exportWindowEndsAt = windowEnds,
                outstandingInvoiceCount = cmd.OutstandingInvoices,
            }),
            new AuditEntry("TenantArchived", "Tenant", actor.TenantId, version + 1, cmd.Reason, new { lifecycle = lifecycle.ToString() },
                new { lifecycle = nameof(TenantLifecycle.Archived), deletionRequested = cmd.DeletionRequested }),
        ], ct);
        var row = await ReadAsync(conn, tx, id, ct);
        await tx.CommitAsync(ct);
        return row;
    }

    public async Task SetRetentionHoldAsync(PlatformCaller caller, Guid tenantId, int expectedVersion, RetentionHoldRequest request, DateTimeOffset now,
        CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var (lifecycle, version) = await LockTenantAsync(conn, tx, tenantId, ct);
        if (version != expectedVersion)
            throw AppException.VersionConflict();
        if (lifecycle == TenantLifecycle.Deleted)
            throw AppException.InvalidState("The workspace is deleted; its retained records keep their own retention rules.");
        var before = await Sql.ScalarAsync<bool?>(conn, tx, "SELECT retention_hold FROM ti.tenant WHERE id = @t", ct, ("t", tenantId)) ?? false;
        if (before == request.Hold)
            throw AppException.InvalidState(request.Hold ? "A retention hold is already set." : "There is no retention hold to clear.");
        await Sql.ExecAsync(conn, tx, """
            UPDATE ti.tenant SET retention_hold = @hold, retention_hold_reason = @reason, retention_hold_reference = @reference,
                   updated_at = @now, version = version + 1
            WHERE id = @t
            """, ct, ("hold", request.Hold), ("reason", request.Hold ? request.Reason : (object)DBNull.Value),
            ("reference", request.Hold ? request.Reference! : (object)DBNull.Value), ("now", now), ("t", tenantId));
        await AuditAppender.AppendAsync(conn, tx, tenantId, Actor(caller), now,
            [new AuditEntry(request.Hold ? "RetentionHoldSet" : "RetentionHoldCleared", "Tenant", tenantId, version + 1, request.Reason,
                new { retentionHold = before }, new { retentionHold = request.Hold, reference = request.Reference })], ct);
        await tx.CommitAsync(ct);
    }

    public async Task WithdrawAsync(PlatformCaller caller, Guid tenantId, int expectedVersion, string reason, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var (lifecycle, version) = await LockTenantAsync(conn, tx, tenantId, ct);
        if (version != expectedVersion)
            throw AppException.VersionConflict();
        var open = await OpenRecordAsync(conn, tx, tenantId, ct)
            ?? throw AppException.InvalidState("There is no open closure to withdraw.");
        if (open.Status == ClosureStatus.DeletionPending || lifecycle is not (TenantLifecycle.Archived or TenantLifecycle.Ready))
            throw AppException.InvalidState("Deletion has started; it cannot be withdrawn.");
        await Sql.ExecAsync(conn, tx, """
            UPDATE cp.deletion_record SET status = 'Withdrawn', updated_at = @now, version = version + 1 WHERE id = @id
            """, ct, ("now", now), ("id", open.Id));
        var audit = new List<AuditEntry>
        {
            new("TenantClosureWithdrawn", "Closure", open.Id, null, reason, new { status = open.Status.ToString() },
                new { status = nameof(ClosureStatus.Withdrawn) }),
        };
        if (lifecycle == TenantLifecycle.Archived)
        {
            await Sql.ExecAsync(conn, tx, """
                UPDATE ti.tenant SET lifecycle = 'Ready', archived_at = NULL, deletion_requested_at = NULL, updated_at = @now, version = version + 1
                WHERE id = @t
                """, ct, ("now", now), ("t", tenantId));
            audit.Add(new AuditEntry("TenantRestoredFromArchive", "Tenant", tenantId, version + 1, reason,
                new { lifecycle = nameof(TenantLifecycle.Archived) }, new { lifecycle = nameof(TenantLifecycle.Ready) }));
        }
        await AuditAppender.AppendAsync(conn, tx, tenantId, Actor(caller), now, audit, ct);
        await tx.CommitAsync(ct);
    }

    public async Task ApproveDeletionAsync(PlatformCaller caller, Guid tenantId, int expectedVersion, string reason, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var (lifecycle, version) = await LockTenantAsync(conn, tx, tenantId, ct);
        if (version != expectedVersion)
            throw AppException.VersionConflict();
        var open = await OpenRecordAsync(conn, tx, tenantId, ct);
        if (open is not { Status: ClosureStatus.ExportWindow } || lifecycle != TenantLifecycle.Archived)
            throw AppException.InvalidState("Only a closed workspace in its export window can be approved for deletion.");
        if (!open.DeletionRequested)
            throw AppException.InvalidState("The owner has not asked for deletion; without that request only an approved retention policy may delete (D10).");
        if (await Sql.ScalarAsync<bool?>(conn, tx, "SELECT retention_hold FROM ti.tenant WHERE id = @t", ct, ("t", tenantId)) == true)
            throw new AppException(ErrorCode.RetentionBlocked, "A retention or legal hold is set; deletion waits until it is cleared.");
        var approved = await Sql.ExecAsync(conn, tx, """
            UPDATE cp.deletion_record SET approved_by = @by, approved_at = @now, updated_at = @now, version = version + 1
            WHERE id = @id AND approved_at IS NULL
            """, ct, ("by", caller.Subject), ("now", now), ("id", open.Id));
        if (approved == 0)
            throw AppException.InvalidState("The deletion is already approved.");
        await AuditAppender.AppendAsync(conn, tx, tenantId, Actor(caller), now,
            [new AuditEntry("TenantDeletionApproved", "Closure", open.Id, null, reason, null,
                new { approvedBy = caller.Subject, exportWindowEndsAt = open.ExportWindowEndsAt })], ct);
        await tx.CommitAsync(ct);
    }

    private static async Task<(TenantLifecycle Lifecycle, int Version)> LockTenantAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT lifecycle::text, version FROM ti.tenant WHERE id = @t FOR UPDATE", conn, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            throw AppException.NotFound("Tenant");
        return (Enum.Parse<TenantLifecycle>(r.GetString(0)), r.GetInt32(1));
    }

    private static async Task<ClosureRow?> OpenRecordAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"""
            SELECT {RecordColumns} FROM cp.deletion_record
            WHERE tenant_id = @t AND status IN ('Requested','ExportWindow','Blocked','DeletionPending') FOR UPDATE
            """, conn, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        return await ReadOneAsync(cmd, ct);
    }

    private static async Task<ClosureRow> ReadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {RecordColumns} FROM cp.deletion_record WHERE id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        return (await ReadOneAsync(cmd, ct))!;
    }

    internal static async Task<ClosureRow?> ReadOneAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;
        return new ClosureRow(r.GetGuid(0), r.GetGuid(1), Enum.Parse<ClosureStatus>(r.GetString(2)), r.GetFieldValue<DateTimeOffset>(3), r.GetString(4),
            r.GetBoolean(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetGuid(7), r.GetInt32(8),
            ClosureService.ParseBlockers(r.IsDBNull(9) ? null : r.GetString(9)), r.IsDBNull(10) ? null : r.GetGuid(10));
    }

    internal static string HoldResults(IReadOnlyList<ClosureBlocker> blockers, int outstandingInvoices, bool? retentionHold) =>
        JsonSerializer.Serialize(new { blockers, outstandingInvoiceCount = outstandingInvoices, retentionHold }, JsonDefaults.Options);

    private static AuditActor Actor(BillingActor a) => new(a.Subject, ActorType.User, a.RequestId, a.CorrelationId);

    private static AuditActor Actor(PlatformCaller c) => new(c.Subject, ActorType.User, c.RequestId, null);
}
