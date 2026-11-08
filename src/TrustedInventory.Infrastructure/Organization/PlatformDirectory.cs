using System.Data;
using Npgsql;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Organization;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Infrastructure.Persistence;
using TrustedInventory.Infrastructure.Saas;

namespace TrustedInventory.Infrastructure.Organization;

/// <summary>
/// Platform-console reads on ti_control (DEC-035): the tenant row, its subscription summary and provisioning job.
/// ti_control has no stock or catalog privileges, so these queries cannot reach tenant data (SAS-004).
/// </summary>
public sealed class PlatformDirectory(ControlDataSource dataSource) : IPlatformDirectory
{
    private const string TenantSelect = """
        SELECT t.id, t.version, t.created_at, t.created_by, t.updated_at, t.name, coalesce(t.slug, ''), coalesce(t.lifecycle::text, 'Ready'),
               coalesce(t.home_region, ''), t.ready_at, t.archived_at, t.deletion_requested_at, t.retention_hold,
               t.security_suspended_at, t.security_suspension_reason, t.security_suspended_by,
               s.state::text, s.trial_end, s.period_end, s.grace_end, coalesce(s.cancel_at_period_end, false), pv.plan_code,
               j.id, j.status::text
        FROM ti.tenant t
        LEFT JOIN cp.subscription s ON s.tenant_id = t.id
        LEFT JOIN cp.plan_version pv ON pv.id = s.plan_version_id
        LEFT JOIN cp.provisioning_job j ON j.tenant_id = t.id
        """;

    public async Task<IReadOnlyList<PlatformTenantRow>> ListTenantsAsync(TenantLifecycle? lifecycle, string? search, CursorPosition? after,
        int take, CancellationToken ct)
    {
        await using var cmd = dataSource.Source.CreateCommand(TenantSelect + """

            WHERE (@lifecycle::text IS NULL OR t.lifecycle::text = @lifecycle::text)
              AND (@search::text IS NULL OR lower(t.name) LIKE '%' || lower(@search::text) || '%' OR lower(t.slug) LIKE '%' || lower(@search::text) || '%')
              AND (@after_at::timestamptz IS NULL OR t.created_at < @after_at OR (t.created_at = @after_at AND t.id < @after_id))
            ORDER BY t.created_at DESC, t.id DESC
            LIMIT @take
            """);
        cmd.Parameters.AddWithValue("lifecycle", (object?)lifecycle?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("search", string.IsNullOrWhiteSpace(search) ? DBNull.Value : search.Trim());
        cmd.Parameters.Add(new NpgsqlParameter("after_at", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)after?.CreatedAt ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("after_id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)after?.Id ?? DBNull.Value });
        cmd.Parameters.AddWithValue("take", take);
        return await ReadTenantsAsync(cmd, ct);
    }

    public async Task<PlatformTenantRow?> GetTenantAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = dataSource.Source.CreateCommand(TenantSelect + "\nWHERE t.id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return (await ReadTenantsAsync(cmd, ct)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<ProvisioningJobDto>> ListProvisioningJobsAsync(string? status, CursorPosition? after, int take,
        CancellationToken ct)
    {
        await using var cmd = dataSource.Source.CreateCommand($"""
            {JobSelect}
            WHERE (@status::text IS NULL OR status::text = @status::text)
              AND (@after_at::timestamptz IS NULL OR created_at < @after_at OR (created_at = @after_at AND id < @after_id))
            ORDER BY created_at DESC, id DESC
            LIMIT @take
            """);
        cmd.Parameters.AddWithValue("status", (object?)status ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("after_at", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)after?.CreatedAt ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("after_id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)after?.Id ?? DBNull.Value });
        cmd.Parameters.AddWithValue("take", take);
        return await ReadJobsAsync(cmd, ct);
    }

    public async Task<ProvisioningJobDto?> RetryProvisioningJobAsync(Guid id, CancellationToken ct)
    {
        // Only a Failed job is reset; retrying a job that is already pending or done changes nothing.
        await using (var reset = dataSource.Source.CreateCommand("""
            UPDATE cp.provisioning_job
            SET status = 'Pending', failure_code = NULL, attempts = 0, next_attempt_at = now(),
                lease_owner = NULL, lease_expires_at = NULL, updated_at = now()
            WHERE id = @id AND status = 'Failed'
            """))
        {
            reset.Parameters.AddWithValue("id", id);
            await reset.ExecuteNonQueryAsync(ct);
        }
        await using var cmd = dataSource.Source.CreateCommand(JobSelect + "\nWHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return (await ReadJobsAsync(cmd, ct)).SingleOrDefault();
    }

    private const string JobSelect = """
        SELECT id, tenant_id, status::text, current_step::text, failure_code, ready_at, created_at, updated_at, attempts
        FROM cp.provisioning_job
        """;

    private static async Task<List<ProvisioningJobDto>> ReadJobsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var jobs = new List<ProvisioningJobDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var status = r.GetString(2);
            jobs.Add(new ProvisioningJobDto(r.GetGuid(0), r.GetGuid(1), status, r.IsDBNull(3) ? null : r.GetString(3),
                Retryable: status == "Failed", r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
                r.GetFieldValue<DateTimeOffset>(6), r.GetFieldValue<DateTimeOffset>(7)));
        }
        return jobs;
    }

    private static async Task<List<PlatformTenantRow>> ReadTenantsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var rows = new List<PlatformTenantRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            DateTimeOffset? At(int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
            string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            rows.Add(new PlatformTenantRow(
                r.GetGuid(0), r.GetInt32(1), r.GetFieldValue<DateTimeOffset>(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4),
                r.GetString(5), r.GetString(6), Enum.Parse<TenantLifecycle>(r.GetString(7)), r.GetString(8), At(9), At(10), At(11),
                r.GetBoolean(12), At(13), Text(14), Text(15), Text(16) is { } st ? Enum.Parse<SubscriptionState>(st) : null,
                At(17), At(18), At(19), r.GetBoolean(20), Text(21), r.IsDBNull(22) ? null : r.GetGuid(22), Text(23)));
        }
        return rows;
    }
}

/// <summary>
/// Reads one workspace's access state and plan code in a read-only tenant transaction (cp.tenant_access only
/// answers for the tenant in app.tenant_id). The plan code is visible only for published public plans.
/// </summary>
public sealed class WorkspaceStatusReader(AppDataSource dataSource) : IWorkspaceStatusReader
{
    public async Task<(TenantAccessState? State, string? PlanCode)> ReadAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await dataSource.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using (var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx))
            await ro.ExecuteNonQueryAsync(ct);
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", tenantId.ToString());
            await set.ExecuteNonQueryAsync(ct);
        }
        var state = await TenantAccessReader.QueryAsync(conn, tx, ct);
        string? plan;
        await using (var cmd = new NpgsqlCommand("""
            SELECT pv.plan_code FROM cp.subscription s JOIN cp.plan_version pv ON pv.id = s.plan_version_id
            """, conn, tx))
            plan = await cmd.ExecuteScalarAsync(ct) as string;
        await tx.CommitAsync(ct);
        return (state, plan);
    }
}
