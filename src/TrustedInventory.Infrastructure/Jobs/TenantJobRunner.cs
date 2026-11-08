using Npgsql;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Jobs;

/// <summary>
/// Gives background jobs an explicit, authorised tenant context (TEN-004). Tenant ids come from the ti_platform
/// pool, which can read only provisioning tables; every read or write of tenant data then runs in a
/// per-tenant ti_app transaction with app.tenant_id set, exactly like an API request.
/// </summary>
public sealed class TenantJobRunner(PlatformDataSource platform, AppDataSource app, TimeProvider clock)
{
    public const string Subject = "system:worker";

    /// <summary>
    /// Tenants the job runs for, by the state matrix's background-work table (DEC-043). Reads lifecycle and the
    /// security suspension, never the v2.0 status column (dropped by the 0005 contract migration). The returned
    /// status only feeds the legacy CallerContext.RequireMutable check.
    /// </summary>
    public async Task<IReadOnlyList<(Guid Id, TenantStatus Status)>> TenantsAsync(BackgroundJob job, CancellationToken ct)
    {
        await using var cmd = platform.Source.CreateCommand(
            "SELECT id, lifecycle::text, security_suspended_at IS NOT NULL FROM ti.tenant ORDER BY id");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var tenants = new List<(Guid, TenantStatus)>();
        while (await reader.ReadAsync(ct))
        {
            // A missing or unknown lifecycle fails closed: the tenant gets no background work.
            if (reader.IsDBNull(1) || !Enum.TryParse<TenantLifecycle>(reader.GetString(1), out var lifecycle)
                || !BackgroundWork.RunsFor(job, lifecycle))
                continue;
            tenants.Add((reader.GetGuid(0), reader.GetBoolean(2) ? TenantStatus.Suspended : TenantStatus.Active));
        }
        return tenants;
    }

    /// <summary>A unit of work acting as the system worker inside one tenant. Jobs hold no user permissions.</summary>
    public TenantUnitOfWork UnitOfWorkFor(Guid tenantId, TenantStatus status) =>
        new(app, new FixedCallerAccessor(new CallerContext(Subject, ActorType.System, tenantId, status,
            new Dictionary<string, PermissionGrant>())), clock);

    /// <summary>The open connection and transaction of a job's tenant session (same assembly only).</summary>
    internal static (NpgsqlConnection Connection, NpgsqlTransaction Transaction) Raw(Application.Persistence.ITenantSession session)
    {
        var s = (TenantUnitOfWork.Session)session;
        return (s.Connection, s.Transaction);
    }

    private sealed class FixedCallerAccessor(CallerContext caller) : ICallerAccessor
    {
        public CallerContext Caller { get; } = caller;
    }
}
