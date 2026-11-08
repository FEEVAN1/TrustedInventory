using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Saas;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Administration;

public sealed record MembershipDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt,
    string UserSubject, string? DisplayName, bool Active) : IVersioned
{
    public static MembershipDto From(Membership m) =>
        new(m.Id, m.Version, m.CreatedAt, m.CreatedBy, m.UpdatedAt, m.UserSubject, m.DisplayName, m.Active);
}

public sealed record RoleGrantDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt,
    Guid MembershipId, RoleName Role, Guid? WarehouseId, string Reason, bool Active, DateTimeOffset? RevokedAt,
    string? RevokedBy) : IVersioned
{
    public static RoleGrantDto From(RoleGrant g) =>
        new(g.Id, g.Version, g.CreatedAt, g.CreatedBy, g.UpdatedAt, g.MembershipId, g.Role, g.WarehouseId, g.Reason,
            g.Active, g.RevokedAt, g.RevokedBy);
}

public sealed record CreateMembership(string UserSubject, string? DisplayName);

public sealed record ReasonRequest(string Reason);

public sealed record CreateRoleGrant(RoleName Role, Guid? WarehouseId, string Reason);

public static class SubjectValidation
{
    /// <summary>OIDC subject: 1..255 characters without surrounding whitespace.</summary>
    public static IRuleBuilderOptions<T, string> ValidSubject<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(255).Must(s => s == s.Trim()).WithMessage("Remove leading or trailing spaces.");
}

public sealed class CreateMembershipValidator : AbstractValidator<CreateMembership>
{
    public CreateMembershipValidator()
    {
        RuleFor(x => x.UserSubject).ValidSubject();
        When(x => x.DisplayName is not null, () => RuleFor(x => x.DisplayName!).ValidName());
    }
}

public sealed class ReasonRequestValidator : AbstractValidator<ReasonRequest>
{
    public ReasonRequestValidator() => RuleFor(x => x.Reason).ValidReason();
}

public sealed class CreateRoleGrantValidator : AbstractValidator<CreateRoleGrant>
{
    public CreateRoleGrantValidator()
    {
        RuleFor(x => x.Reason).ValidReason();
        RuleFor(x => x.Role).IsInEnum().NotEqual(RoleName.PlatformAdministrator)
            .WithMessage("PlatformAdministrator cannot be granted within a tenant.");
        RuleFor(x => x.Role).NotEqual(RoleName.TelemetryIntegration)
            .WithMessage("TelemetryIntegration belongs to device service principals, not users.");
        RuleFor(x => x.Role).NotEqual(RoleName.PlatformBilling)
            .WithMessage("PlatformBilling cannot be granted within a tenant.");
        RuleFor(x => x.WarehouseId).NotEqual(Guid.Empty);
    }
}

/// <summary>
/// Memberships and role grants (section 05, IAM-001). Deactivating a membership removes access on the next
/// request because tenant context is resolved from active memberships only. Every change is audited.
/// Active memberships count as seats (ENT-003). TenantOwner changes only through signup and ownership transfer,
/// and the last owner is protected (SOD-5); only an owner delegates billing (SOD-6).
/// </summary>
public sealed class MembershipService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IQuotaGate quota,
    IValidator<CreateMembership> createValidator,
    IValidator<ReasonRequest> reasonValidator,
    IValidator<CreateRoleGrant> grantValidator)
{
    public Task<MembershipDto> CreateAsync(CreateMembership cmd, CancellationToken ct)
    {
        var caller = Manager();
        caller.RequireMutable();
        createValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            // SAS-005 order: reserve the quota, then domain validation; a rejection rolls the reservation back.
            // No document row is locked first, so the counter lock respects the lock order (migration-design §11.5).
            await quota.ReserveAsync(s, QuotaKey.ActiveSeats, 1, c);
            if (await s.Db.Set<Membership>().AnyAsync(m => m.UserSubject == cmd.UserSubject, c))
                throw AppException.InvalidState("This user already has a membership in the tenant.");
            var membership = Membership.Create(caller.TenantId, cmd.UserSubject, cmd.DisplayName, caller.Subject, s.Now);
            s.Db.Add(membership);
            var dto = MembershipDto.From(membership);
            s.Audit(new AuditEntry("MembershipCreated", "Membership", membership.Id, membership.Version, null, null, dto));
            return dto;
        }, ct);
    }

    public Task<MembershipDto> DeactivateAsync(Guid id, int expectedVersion, ReasonRequest cmd, CancellationToken ct)
    {
        var caller = Manager();
        caller.RequireMutable();
        reasonValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            var membership = await LoadMembership(s, id, c);
            Versioning.EnsureVersion(membership.Version, expectedVersion);
            var before = MembershipDto.From(membership);
            if (await IsLastOwnerAsync(s, membership.Id, c))
                throw AppException.InvalidState("The last owner cannot be removed. Transfer ownership first.");
            DomainGuard.Run(() => membership.Deactivate(caller.Subject, s.Now));
            await quota.ReleaseAsync(s, QuotaKey.ActiveSeats, 1, c);

            var after = MembershipDto.From(membership);
            s.Audit(new AuditEntry("MembershipDeactivated", "Membership", id, membership.Version, cmd.Reason, before, after));
            return after;
        }, ct);
    }

    public Task<MembershipDto> GetAsync(Guid id, CancellationToken ct)
    {
        Manager();
        return uow.ReadAsync(async (s, c) => MembershipDto.From(await LoadMembership(s, id, c)), ct);
    }

    public Task<Page<MembershipDto>> ListAsync(bool? active, PageRequest page, CancellationToken ct)
    {
        Manager();
        return uow.ReadAsync((s, c) =>
        {
            var query = s.Db.Set<Membership>().AsNoTracking();
            if (active is { } a)
                query = query.Where(m => m.Active == a);
            return query.ToPageAsync(page, $"memberships|{active}", cursors, MembershipDto.From, c);
        }, ct);
    }

    public Task<RoleGrantDto> GrantAsync(Guid membershipId, CreateRoleGrant cmd, CancellationToken ct)
    {
        var caller = Manager();
        caller.RequireMutable();
        grantValidator.ValidateOrThrow(cmd);
        RequireRoleAuthority(caller, cmd.Role);

        return uow.WriteAsync(async (s, c) =>
        {
            var membership = await LoadMembership(s, membershipId, c);
            if (cmd.WarehouseId is { } wid && !await s.Db.Set<Warehouse>().AnyAsync(w => w.Id == wid && w.Active, c))
                throw AppException.Validation("warehouseId", "Unknown or retired warehouse.");
            if (await s.Db.Set<RoleGrant>().AnyAsync(g => g.MembershipId == membershipId && g.Role == cmd.Role
                    && g.WarehouseId == cmd.WarehouseId && g.RevokedAt == null, c))
                throw AppException.InvalidState("This role is already granted with the same scope.");

            RoleGrant grant;
            try
            {
                grant = DomainGuard.Run(() =>
                    RoleGrant.Create(caller.TenantId, membership, cmd.Role, cmd.WarehouseId, cmd.Reason, caller.Subject, s.Now));
            }
            catch (SeparationOfDutyException ex)
            {
                throw new AppException(ErrorCode.ApprovalSelf, ex.Message);
            }
            s.Db.Add(grant);

            var dto = RoleGrantDto.From(grant);
            s.Audit(new AuditEntry("RoleGranted", "RoleGrant", grant.Id, grant.Version, cmd.Reason, null, dto));
            return dto;
        }, ct);
    }

    public Task<RoleGrantDto> RevokeAsync(Guid grantId, int expectedVersion, ReasonRequest cmd, CancellationToken ct)
    {
        var caller = Manager();
        caller.RequireMutable();
        reasonValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            var grant = await s.Db.Set<RoleGrant>().SingleOrDefaultAsync(g => g.Id == grantId, c)
                ?? throw AppException.NotFound("Role grant");
            RequireRoleAuthority(caller, grant.Role);
            Versioning.EnsureVersion(grant.Version, expectedVersion);
            var before = RoleGrantDto.From(grant);
            DomainGuard.Run(() => grant.Revoke(caller.Subject, s.Now));

            var after = RoleGrantDto.From(grant);
            s.Audit(new AuditEntry("RoleRevoked", "RoleGrant", grantId, grant.Version, cmd.Reason, before, after));
            return after;
        }, ct);
    }

    public Task<Page<RoleGrantDto>> ListGrantsAsync(Guid membershipId, bool? active, PageRequest page, CancellationToken ct)
    {
        Manager();
        return uow.ReadAsync(async (s, c) =>
        {
            await LoadMembership(s, membershipId, c);
            var query = s.Db.Set<RoleGrant>().AsNoTracking().Where(g => g.MembershipId == membershipId);
            if (active is { } a)
                query = a ? query.Where(g => g.RevokedAt == null) : query.Where(g => g.RevokedAt != null);
            return await query.ToPageAsync(page, $"role-grants|{membershipId}|{active}", cursors, RoleGrantDto.From, c);
        }, ct);
    }

    /// <summary>SOD-5 and SOD-6: who may grant or revoke a role through these routes.</summary>
    private static void RequireRoleAuthority(CallerContext caller, RoleName role)
    {
        if (role == RoleName.TenantOwner)
            throw AppException.InvalidState("Ownership changes only through signup or an accepted ownership transfer.");
        if (role == RoleName.BillingAdministrator && !caller.Has(Permissions.BillingDelegate))
            throw AppException.Forbidden("Only the workspace owner can delegate billing.");
    }

    /// <summary>True when this membership holds the only active TenantOwner grant among active memberships.</summary>
    private static async Task<bool> IsLastOwnerAsync(ITenantSession s, Guid membershipId, CancellationToken ct)
    {
        var ownerMemberships = await s.Db.Set<RoleGrant>()
            .Where(g => g.Role == RoleName.TenantOwner && g.RevokedAt == null)
            .Join(s.Db.Set<Membership>().Where(m => m.Active), g => g.MembershipId, m => m.Id, (g, m) => m.Id)
            .Distinct()
            .ToListAsync(ct);
        return ownerMemberships.Count == 1 && ownerMemberships[0] == membershipId;
    }

    private CallerContext Manager()
    {
        var caller = callers.Caller;
        caller.Require(Permissions.TenantMembersManage);
        return caller;
    }

    private static async Task<Membership> LoadMembership(ITenantSession s, Guid id, CancellationToken ct) =>
        await s.Db.Set<Membership>().SingleOrDefaultAsync(m => m.Id == id, ct) ?? throw AppException.NotFound("Membership");
}
