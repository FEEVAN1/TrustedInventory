using System.Data;
using Npgsql;
using TrustedInventory.Application.Billing;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Organization;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Infrastructure.Audit;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Billing;

/// <summary>
/// Complimentary grants on the ti_control pool (DEC-094). The tenant row is locked for a request so two operators cannot open two grants
/// for one workspace; the table's trigger keeps a request, its approval and its revocation unchangeable once written.
/// </summary>
public sealed class ComplimentaryAccessStore(ControlDataSource control) : IComplimentaryAccessStore
{
    private const string Columns = """
        c.id, c.version, c.tenant_id, t.name, c.plan_version_id, p.plan_code, c.expires_at, c.reason, c.requested_by, c.requested_at,
        c.approved_by, c.approved_at, c.approval_reason, c.revoked_by, c.revoked_at, c.revoke_reason
        """;

    private const string From = "FROM cp.complimentary_access c JOIN ti.tenant t ON t.id = c.tenant_id JOIN cp.plan_version p ON p.id = c.plan_version_id";

    public async Task<IReadOnlyList<ComplimentaryAccessDto>> ListAsync(Guid? tenantId, ComplimentaryAccessStatus? status, DateTimeOffset now,
        CursorPosition? after, int take, CancellationToken ct)
    {
        await using var cmd = control.Source.CreateCommand($"""
            SELECT {Columns} {From}
            WHERE (@tenant::uuid IS NULL OR c.tenant_id = @tenant)
              AND (@status::text IS NULL OR @status = CASE
                    WHEN c.revoked_at IS NOT NULL THEN 'Revoked'
                    WHEN @now >= c.expires_at THEN 'Expired'
                    WHEN c.approved_at IS NOT NULL THEN 'Active'
                    ELSE 'Pending' END)
              AND (@at::timestamptz IS NULL OR (c.requested_at, c.id) < (@at, @id))
            ORDER BY c.requested_at DESC, c.id DESC LIMIT @take
            """);
        cmd.Parameters.AddWithValue("tenant", (object?)tenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", (object?)status?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("at", (object?)after?.CreatedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", after?.Id ?? Guid.Empty);
        cmd.Parameters.AddWithValue("take", take);
        var rows = new List<ComplimentaryAccessDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(Read(r, now));
        return rows;
    }

    public async Task<ComplimentaryAccessDto> RequestAsync(PlatformCaller caller, CreateComplimentaryAccessRequest request, DateTimeOffset now,
        CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var lifecycle = await Sql.ScalarAsync<string>(conn, tx, "SELECT coalesce(lifecycle::text, 'Ready') FROM ti.tenant WHERE id = @t FOR UPDATE", ct,
            ("t", request.TenantId)) ?? throw AppException.NotFound("Workspace");
        if (lifecycle is nameof(TenantLifecycle.Archived) or nameof(TenantLifecycle.DeletionPending) or nameof(TenantLifecycle.Deleted))
            throw AppException.InvalidState("This workspace is closed; complimentary access cannot reopen it.");
        var published = await Sql.ScalarAsync<bool?>(conn, tx, "SELECT published_at IS NOT NULL FROM cp.plan_version WHERE id = @p", ct,
            ("p", request.PlanVersionId)) ?? throw AppException.NotFound("Plan version");
        if (!published)
            throw AppException.InvalidState("Only a published plan version can be granted.");
        var open = await Sql.ScalarAsync<bool>(conn, tx,
            "SELECT EXISTS (SELECT 1 FROM cp.complimentary_access WHERE tenant_id = @t AND revoked_at IS NULL AND expires_at > @now)", ct,
            ("t", request.TenantId), ("now", now));
        if (open)
            throw AppException.InvalidState("This workspace already has a pending or active complimentary grant; revoke it first.");

        var id = await Sql.ScalarAsync<Guid>(conn, tx, """
            INSERT INTO cp.complimentary_access (tenant_id, plan_version_id, features, limits, expires_at, reason, requested_by, requested_at)
            SELECT @t, p.id, p.features, p.quotas, @exp, @reason, @by, @now FROM cp.plan_version p WHERE p.id = @p
            RETURNING id
            """, ct, ("t", request.TenantId), ("p", request.PlanVersionId), ("exp", request.ExpiresAt), ("reason", request.Reason),
            ("by", caller.Subject), ("now", now));
        var grant = await FindAsync(conn, tx, id, now, ct);
        await AuditAsync(conn, tx, caller, now, grant, "ComplimentaryAccessRequested", request.Reason, null, ct);
        await tx.CommitAsync(ct);
        return grant;
    }

    public async Task<ComplimentaryAccessDto> ApproveAsync(PlatformCaller caller, Guid id, int expectedVersion, string reason, DateTimeOffset now,
        CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var grant = await LockAsync(conn, tx, id, expectedVersion, now, ct);
        if (grant.Status != ComplimentaryAccessStatus.Pending)
            throw AppException.InvalidState($"Only a pending grant can be approved; this one is {grant.Status}.");
        if (string.Equals(grant.RequestedBy, caller.Subject, StringComparison.Ordinal))
            throw new AppException(ErrorCode.ApprovalSelf, "You requested this grant, so another platform operator must approve it (section 29).");
        await Sql.ExecAsync(conn, tx, """
            UPDATE cp.complimentary_access SET approved_by = @by, approved_at = @now, approval_reason = @reason, version = version + 1 WHERE id = @id
            """, ct, ("by", caller.Subject), ("now", now), ("reason", reason), ("id", id));
        var approved = await FindAsync(conn, tx, id, now, ct);
        await AuditAsync(conn, tx, caller, now, approved, "ComplimentaryAccessApproved", reason, grant, ct);
        await tx.CommitAsync(ct);
        return approved;
    }

    public async Task<ComplimentaryAccessDto> RevokeAsync(PlatformCaller caller, Guid id, int expectedVersion, string reason, DateTimeOffset now,
        CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var grant = await LockAsync(conn, tx, id, expectedVersion, now, ct);
        if (grant.Status is not (ComplimentaryAccessStatus.Pending or ComplimentaryAccessStatus.Active))
            throw AppException.InvalidState($"Only a pending or active grant can be revoked; this one is {grant.Status}.");
        await Sql.ExecAsync(conn, tx, """
            UPDATE cp.complimentary_access SET revoked_by = @by, revoked_at = @now, revoke_reason = @reason, version = version + 1 WHERE id = @id
            """, ct, ("by", caller.Subject), ("now", now), ("reason", reason), ("id", id));
        var revoked = await FindAsync(conn, tx, id, now, ct);
        await AuditAsync(conn, tx, caller, now, revoked, "ComplimentaryAccessRevoked", reason, grant, ct);
        await tx.CommitAsync(ct);
        return revoked;
    }

    private static async Task<ComplimentaryAccessDto> LockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, int expectedVersion,
        DateTimeOffset now, CancellationToken ct)
    {
        var version = await Sql.ScalarAsync<int?>(conn, tx, "SELECT version FROM cp.complimentary_access WHERE id = @id FOR UPDATE", ct, ("id", id))
            ?? throw AppException.NotFound("Complimentary access grant");
        if (version != expectedVersion)
            throw AppException.VersionConflict();
        return await FindAsync(conn, tx, id, now, ct);
    }

    private static async Task<ComplimentaryAccessDto> FindAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, DateTimeOffset now,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} {From} WHERE c.id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            throw AppException.NotFound("Complimentary access grant");
        return Read(r, now);
    }

    private static Task AuditAsync(NpgsqlConnection conn, NpgsqlTransaction tx, PlatformCaller caller, DateTimeOffset now, ComplimentaryAccessDto after,
        string eventType, string reason, ComplimentaryAccessDto? before, CancellationToken ct) =>
        AuditAppender.AppendAsync(conn, tx, after.TenantId, new AuditActor(caller.Subject, ActorType.User, caller.RequestId, null), now,
            [new AuditEntry(eventType, "ComplimentaryAccess", after.Id, after.Version, reason, before is null ? null : Snapshot(before), Snapshot(after))], ct);

    private static object Snapshot(ComplimentaryAccessDto g) =>
        new { status = g.Status.ToString(), planCode = g.PlanCode, planVersionId = g.PlanVersionId, expiresAt = g.ExpiresAt };

    private static ComplimentaryAccessDto Read(NpgsqlDataReader r, DateTimeOffset now)
    {
        DateTimeOffset? At(int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
        string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        var expires = r.GetFieldValue<DateTimeOffset>(6);
        return new ComplimentaryAccessDto(r.GetGuid(0), r.GetInt32(1), r.GetGuid(2), r.GetString(3), r.GetGuid(4), r.GetString(5), expires, r.GetString(7),
            ComplimentaryAccessService.StatusAt(At(11), At(14), expires, now), r.GetString(8), r.GetFieldValue<DateTimeOffset>(9), Text(10), At(11), Text(12),
            Text(13), At(14), Text(15));
    }
}
