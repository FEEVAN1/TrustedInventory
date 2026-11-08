using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Application.Organization;

public sealed record SecuritySuspensionDto(string Reason, DateTimeOffset At, string By);

/// <summary>Tenant schema (section 07, DEC-033): stored lifecycle plus the access mode computed now.</summary>
public sealed record TenantDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, string Name, string Slug,
    TenantLifecycle Lifecycle, string HomeRegion, AccessMode AccessMode, IReadOnlyList<RestrictionReason> RestrictionReasons,
    SecuritySuspensionDto? SecuritySuspension, DateTimeOffset? ReadyAt, DateTimeOffset? ArchivedAt, DateTimeOffset? DeletionRequestedAt,
    bool RetentionHold);

/// <summary>PlatformTenant schema: service metadata only (SAS-004, SOP-006).</summary>
public sealed record PlatformTenantDto(
    TenantDto Tenant, SubscriptionState? SubscriptionState, string? PlanCode, Guid? ProvisioningJobId, string? ProvisioningStatus) : IVersioned
{
    [JsonIgnore]
    public int Version => Tenant.Version;

    public static PlatformTenantDto From(PlatformTenantRow r, DateTimeOffset now)
    {
        var state = r.AccessState;
        var suspension = r.SecuritySuspendedAt is { } at
            ? new SecuritySuspensionDto(r.SecuritySuspensionReason ?? "", at, r.SecuritySuspendedBy ?? "")
            : null;
        var tenant = new TenantDto(r.Id, r.Version, r.CreatedAt, r.CreatedBy, r.UpdatedAt, r.Name, r.Slug, r.Lifecycle, r.HomeRegion,
            state.Mode(now), state.Reasons(now), suspension, r.ReadyAt, r.ArchivedAt, r.DeletionRequestedAt, r.RetentionHold);
        return new PlatformTenantDto(tenant, r.SubscriptionState, r.PlanCode, r.ProvisioningJobId, r.ProvisioningStatus);
    }
}

public sealed record SecuritySuspensionRequest(string Reason);

public sealed record PlatformTenantFilter(TenantLifecycle? Lifecycle, AccessMode? AccessMode, string? Search);

public sealed class SecuritySuspensionRequestValidator : AbstractValidator<SecuritySuspensionRequest>
{
    public SecuritySuspensionRequestValidator() => RuleFor(x => x.Reason).ValidReason();
}

/// <summary>
/// Platform operations console (DEC-026, DEC-037): tenant service metadata and reasoned security suspension.
/// Reads run on the control-plane pool; suspension writes run on ti_platform and are audited in the tenant's own
/// chain. Tenants are created only by signup; payment can never lift a security suspension (BIL-007).
/// </summary>
public sealed class PlatformTenantService(
    IPlatformUnitOfWork uow,
    IPlatformDirectory directory,
    ICursorCodec cursors,
    TimeProvider clock,
    IValidator<SecuritySuspensionRequest> suspensionValidator)
{
    public const string SuspendedEvent = "TenantSecuritySuspended";
    public const string LiftedEvent = "TenantSecuritySuspensionLifted";
    private const int ScanBatch = 200;

    public async Task<PlatformTenantDto> GetAsync(PlatformCaller caller, Guid id, CancellationToken ct)
    {
        caller.RequireAdministrator();
        var row = await directory.GetTenantAsync(id, ct) ?? throw AppException.NotFound("Tenant");
        return PlatformTenantDto.From(row, clock.GetUtcNow());
    }

    public async Task<Page<PlatformTenantDto>> ListAsync(PlatformCaller caller, PlatformTenantFilter filter, PageRequest page, CancellationToken ct)
    {
        caller.RequireAdministrator();
        page.EnsureSupportedSort();
        var limit = page.EffectiveLimit;
        var filterKey = $"platform-tenants|{filter.Lifecycle}|{filter.AccessMode}|{filter.Search}";
        var after = page.Cursor is { } c ? cursors.Decode(c, filterKey) : null;
        var now = clock.GetUtcNow();

        // Access mode is computed, not stored, so that filter is applied while scanning in creation order.
        var items = new List<PlatformTenantDto>();
        string? next = null;
        while (true)
        {
            var rows = await directory.ListTenantsAsync(filter.Lifecycle, filter.Search, after, ScanBatch, ct);
            foreach (var row in rows)
            {
                var dto = PlatformTenantDto.From(row, now);
                if (filter.AccessMode is { } mode && dto.Tenant.AccessMode != mode)
                {
                    after = new CursorPosition(row.CreatedAt, row.Id);
                    continue;
                }
                if (items.Count == limit)
                {
                    var last = items[^1].Tenant;
                    next = cursors.Encode(new CursorPosition(last.CreatedAt, last.Id), filterKey);
                    return new Page<PlatformTenantDto>(items, next);
                }
                items.Add(dto);
                after = new CursorPosition(row.CreatedAt, row.Id);
            }
            if (rows.Count < ScanBatch)
                return new Page<PlatformTenantDto>(items, null);
        }
    }

    public Task<PlatformTenantDto> SuspendAsync(PlatformCaller caller, Guid id, int expectedVersion, SecuritySuspensionRequest cmd,
        CancellationToken ct) => ChangeAsync(caller, id, expectedVersion, cmd, suspend: true, ct);

    public Task<PlatformTenantDto> LiftAsync(PlatformCaller caller, Guid id, int expectedVersion, SecuritySuspensionRequest cmd,
        CancellationToken ct) => ChangeAsync(caller, id, expectedVersion, cmd, suspend: false, ct);

    public async Task<Page<ProvisioningJobDto>> ListProvisioningJobsAsync(PlatformCaller caller, string? status, PageRequest page,
        CancellationToken ct)
    {
        caller.RequireAdministrator();
        page.EnsureSupportedSort();
        var limit = page.EffectiveLimit;
        var filterKey = $"provisioning-jobs|{status}";
        var after = page.Cursor is { } c ? cursors.Decode(c, filterKey) : null;
        var rows = (await directory.ListProvisioningJobsAsync(status, after, limit + 1, ct)).ToList();
        string? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            next = cursors.Encode(new CursorPosition(rows[^1].CreatedAt, rows[^1].Id), filterKey);
        }
        return new Page<ProvisioningJobDto>(rows, next);
    }

    /// <summary>Resumes a failed job; the worker reuses existing records, so a retry never duplicates (ONB-002).</summary>
    public async Task<ProvisioningJobDto> RetryProvisioningJobAsync(PlatformCaller caller, Guid id, CancellationToken ct)
    {
        caller.RequireAdministrator();
        return await directory.RetryProvisioningJobAsync(id, ct) ?? throw AppException.NotFound("Provisioning job");
    }

    private async Task<PlatformTenantDto> ChangeAsync(PlatformCaller caller, Guid id, int expectedVersion, SecuritySuspensionRequest cmd,
        bool suspend, CancellationToken ct)
    {
        caller.RequireAdministrator();
        suspensionValidator.ValidateOrThrow(cmd);

        await uow.WriteAsync(caller, async (s, c) =>
        {
            var tenant = await s.Db.Set<Tenant>().SingleOrDefaultAsync(t => t.Id == id, c) ?? throw AppException.NotFound("Tenant");
            Versioning.EnsureVersion(tenant.Version, expectedVersion);
            var before = Snapshot(tenant);
            DomainGuard.Run(() =>
            {
                if (suspend)
                    tenant.SuspendForSecurity(cmd.Reason, caller.Subject, s.Now);
                else
                    tenant.LiftSecuritySuspension(s.Now);
            });
            await s.Db.SaveChangesAsync(c);
            s.Audit(tenant.Id, new AuditEntry(suspend ? SuspendedEvent : LiftedEvent, "Tenant", tenant.Id, tenant.Version, cmd.Reason,
                before, Snapshot(tenant)));
            return true;
        }, ct);

        return await GetAsync(caller, id, ct);
    }

    private static object Snapshot(Tenant t) => new
    {
        t.Id,
        t.Version,
        Lifecycle = t.Lifecycle.ToString(),
        SecuritySuspended = t.IsSecuritySuspended,
        t.SecuritySuspensionReason,
        t.SecuritySuspendedBy,
    };
}
