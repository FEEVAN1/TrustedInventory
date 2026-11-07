using FluentValidation;
using TrustedInventory.Application.Administration;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Audit;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Organization;

public sealed record TenantDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt,
    string Name, TenantStatus Status, string? SuspendedReason) : IVersioned
{
    public static TenantDto From(Tenant t, string? suspendedReason) =>
        new(t.Id, t.Version, t.CreatedAt, t.CreatedBy, t.UpdatedAt, t.Name, t.Status,
            t.Status == TenantStatus.Suspended ? suspendedReason : null);
}

public sealed record CreateTenant(string Name, string InitialAdministratorSubject);

public sealed record TenantStatusChange(string Reason);

public sealed class CreateTenantValidator : AbstractValidator<CreateTenant>
{
    public CreateTenantValidator()
    {
        RuleFor(x => x.Name).ValidName();
        RuleFor(x => x.InitialAdministratorSubject).ValidSubject();
    }
}

public sealed class TenantStatusChangeValidator : AbstractValidator<TenantStatusChange>
{
    public TenantStatusChangeValidator() => RuleFor(x => x.Reason).ValidReason();
}

/// <summary>
/// Tenant provisioning and suspension (TEN-004, section 06). Runs on the ti_platform pool. Provisioning is
/// atomic: tenant, audit stream, first administrator membership and grant, and their audit events.
/// </summary>
public sealed class PlatformTenantService(
    IPlatformUnitOfWork uow,
    ICursorCodec cursors,
    IValidator<CreateTenant> createValidator,
    IValidator<TenantStatusChange> statusValidator)
{
    public const string SuspendedEvent = "TenantSuspended";

    public Task<TenantDto> CreateAsync(PlatformCaller caller, CreateTenant cmd, CancellationToken ct)
    {
        caller.RequireAdministrator();
        createValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(caller, async (s, c) =>
        {
            var tenant = Tenant.Create(cmd.Name, caller.Subject, s.Now);
            var membership = Membership.Create(tenant.Id, cmd.InitialAdministratorSubject, null, caller.Subject, s.Now);
            var grant = RoleGrant.Create(tenant.Id, membership, RoleName.TenantAdministrator, null,
                "Initial tenant administrator", caller.Subject, s.Now);
            s.Db.AddRange(tenant, membership, grant);
            await s.Db.SaveChangesAsync(c);
            await s.CreateAuditStreamAsync(tenant.Id, c);

            var dto = TenantDto.From(tenant, null);
            s.Audit(tenant.Id, new AuditEntry("TenantCreated", "Tenant", tenant.Id, tenant.Version, null, null, dto));
            s.Audit(tenant.Id, new AuditEntry("MembershipCreated", "Membership", membership.Id, membership.Version, null, null,
                MembershipDto.From(membership)));
            s.Audit(tenant.Id, new AuditEntry("RoleGranted", "RoleGrant", grant.Id, grant.Version, grant.Reason, null,
                RoleGrantDto.From(grant)));
            return dto;
        }, ct);
    }

    public Task<TenantDto> SuspendAsync(PlatformCaller caller, Guid id, int expectedVersion, TenantStatusChange cmd, CancellationToken ct) =>
        ChangeStatusAsync(caller, id, expectedVersion, cmd, suspend: true, ct);

    public Task<TenantDto> ReactivateAsync(PlatformCaller caller, Guid id, int expectedVersion, TenantStatusChange cmd, CancellationToken ct) =>
        ChangeStatusAsync(caller, id, expectedVersion, cmd, suspend: false, ct);

    public Task<TenantDto> GetAsync(PlatformCaller caller, Guid id, CancellationToken ct)
    {
        caller.RequireAdministrator();
        return uow.ReadAsync(caller, async (s, c) =>
        {
            var tenant = await Load(s, id, c);
            return TenantDto.From(tenant, await SuspendedReasonAsync(s, tenant, c));
        }, ct);
    }

    public Task<Page<TenantDto>> ListAsync(PlatformCaller caller, TenantStatus? status, PageRequest page, CancellationToken ct)
    {
        caller.RequireAdministrator();
        page.EnsureSupportedSort();
        var limit = page.EffectiveLimit;
        var filterKey = $"platform-tenants|{status}";

        return uow.ReadAsync(caller, async (s, c) =>
        {
            var query = s.Db.Set<Tenant>().AsNoTracking();
            if (status is { } st)
                query = query.Where(t => t.Status == st);
            if (page.Cursor is { } cursor)
            {
                var p = cursors.Decode(cursor, filterKey);
                query = query.Where(t => t.CreatedAt < p.CreatedAt || (t.CreatedAt == p.CreatedAt && t.Id.CompareTo(p.Id) < 0));
            }
            var rows = await query.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Take(limit + 1).ToListAsync(c);
            string? next = null;
            if (rows.Count > limit)
            {
                rows.RemoveAt(limit);
                next = cursors.Encode(new CursorPosition(rows[^1].CreatedAt, rows[^1].Id), filterKey);
            }

            var items = new List<TenantDto>(rows.Count);
            foreach (var t in rows)
                items.Add(TenantDto.From(t, await SuspendedReasonAsync(s, t, c)));
            return new Page<TenantDto>(items, next);
        }, ct);
    }

    private Task<TenantDto> ChangeStatusAsync(PlatformCaller caller, Guid id, int expectedVersion, TenantStatusChange cmd,
        bool suspend, CancellationToken ct)
    {
        caller.RequireAdministrator();
        statusValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(caller, async (s, c) =>
        {
            var tenant = await Load(s, id, c);
            Versioning.EnsureVersion(tenant.Version, expectedVersion);
            var before = TenantDto.From(tenant, await SuspendedReasonAsync(s, tenant, c));
            DomainGuard.Run(() =>
            {
                if (suspend)
                    tenant.Suspend(s.Now);
                else
                    tenant.Reactivate(s.Now);
            });
            await s.Db.SaveChangesAsync(c);

            var after = TenantDto.From(tenant, suspend ? cmd.Reason : null);
            s.Audit(tenant.Id, new AuditEntry(suspend ? SuspendedEvent : "TenantReactivated", "Tenant", tenant.Id, tenant.Version,
                cmd.Reason, before, after));
            return after;
        }, ct);
    }

    private static async Task<Tenant> Load(IPlatformSession s, Guid id, CancellationToken ct) =>
        await s.Db.Set<Tenant>().SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw AppException.NotFound("Tenant");

    /// <summary>The suspension reason lives in the tenant's audit chain, which is its single source of record.</summary>
    private static async Task<string?> SuspendedReasonAsync(IPlatformSession s, Tenant tenant, CancellationToken ct) =>
        tenant.Status != TenantStatus.Suspended
            ? null
            : await s.Db.Set<StoredAuditEvent>().AsNoTracking()
                .Where(e => e.TenantId == tenant.Id && e.EventType == SuspendedEvent)
                .OrderByDescending(e => e.Sequence)
                .Select(e => e.Reason)
                .FirstOrDefaultAsync(ct);
}
