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

public sealed record RoleScope(RoleName Role, Guid? WarehouseId);

public sealed record InvitationDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt,
    string Email, IReadOnlyList<RoleScope> Roles, InvitationStatus Status, DateTimeOffset ExpiresAt,
    string? AcceptedBy, DateTimeOffset? AcceptedAt) : IVersioned
{
    public static InvitationDto From(Invitation i, DateTimeOffset now) =>
        new(i.Id, i.Version, i.CreatedAt, i.CreatedBy, i.UpdatedAt, i.EmailNormalized,
            i.Scopes.Select(s => new RoleScope(s.Role, s.WarehouseId)).ToList(), i.EffectiveStatus(now), i.ExpiresAt,
            i.AcceptedBySubject, i.AcceptedAt);
}

public sealed record CreateInvitation(string Email, IReadOnlyList<RoleScope> Roles);

public sealed record AcceptInvitation(string Token);

/// <summary>The contract's MeMembership for the workspace the caller just joined.</summary>
public sealed record JoinedMembership(
    Guid MembershipId, Guid TenantId, string TenantName, IReadOnlyList<RoleScope> Roles,
    IReadOnlyList<JoinedPermissionScope> Permissions, string TenantSlug, TenantLifecycle Lifecycle, AccessMode AccessMode,
    IReadOnlyList<RestrictionReason> RestrictionReasons, string? PlanCode);

public sealed record JoinedPermissionScope(string Permission, IReadOnlyList<Guid>? WarehouseIds);

public sealed class CreateInvitationValidator : AbstractValidator<CreateInvitation>
{
    public CreateInvitationValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254).EmailAddress().WithMessage("Enter a valid email address.");
        RuleFor(x => x.Roles).NotNull().Must(r => r is { Count: >= 1 and <= 20 }).WithMessage("Invite with 1 to 20 roles.")
            .Must(r => r is null || r.Distinct().Count() == r.Count).WithMessage("Roles must be unique.");
        RuleForEach(x => x.Roles).ChildRules(scope =>
        {
            scope.RuleFor(s => s.Role).IsInEnum()
                .Must(r => !Invitation.ForbiddenRoles.Contains(r)).WithMessage("This role cannot be granted by invitation.");
            scope.RuleFor(s => s.WarehouseId).NotEqual(Guid.Empty);
        });
    }
}

public sealed class AcceptInvitationValidator : AbstractValidator<AcceptInvitation>
{
    public AcceptInvitationValidator() => RuleFor(x => x.Token).NotEmpty().Length(32, 256);
}

/// <summary>
/// Team invitations (ONB-005). Tokens are single-use, bound to one verified email, expire after 72 hours and
/// reserve a seat atomically on acceptance (AT-ONB-02). Acceptance runs inside the target tenant's own
/// transaction as ti_app, so RLS and the seat-counter lock apply exactly as for any tenant write.
/// </summary>
public sealed class InvitationService(
    ITenantUnitOfWork uow,
    ITenantUnitOfWorkFactory uowFactory,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IInvitationTokens tokens,
    IInvitationLookup lookup,
    ITenantAccessReader accessReader,
    IQuotaGate quota,
    TimeProvider clock,
    SaasOptions saas,
    IValidator<CreateInvitation> createValidator,
    IValidator<ReasonRequest> reasonValidator,
    IValidator<AcceptInvitation> acceptValidator)
{
    // Same answer for an unknown, used, revoked or expired token: the token holder learns nothing more.
    private static AppException NotValid() => AppException.NotFound("Invitation");

    public Task<InvitationDto> CreateAsync(CreateInvitation cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.TenantMembersManage);
        createValidator.ValidateOrThrow(cmd);
        // SOD-6: billing delegation is the owner's decision, never a tenant administrator's.
        if (cmd.Roles.Any(r => r.Role == RoleName.BillingAdministrator) && !caller.Has(Permissions.BillingDelegate))
            throw AppException.Forbidden("Only the workspace owner can invite a billing administrator.");
        var email = cmd.Email.Trim().ToLowerInvariant();

        return uow.WriteAsync(async (s, c) =>
        {
            await EnforceInvitationLimitAsync(s, caller.TenantId, c);
            foreach (var wid in cmd.Roles.Where(r => r.WarehouseId is not null).Select(r => r.WarehouseId!.Value).Distinct())
                if (!await s.Db.Set<Warehouse>().AnyAsync(w => w.Id == wid && w.Active, c))
                    throw AppException.Validation("roles", "Unknown or retired warehouse.");
            if (await s.Db.Set<Invitation>().AnyAsync(i => i.EmailNormalized == email && i.Status == InvitationStatus.Pending
                    && i.ExpiresAt > s.Now, c))
                throw AppException.InvalidState("A pending invitation already exists for this email address.");
            await ExpireLapsedAsync(s, email, c);

            var id = Guid.CreateVersion7();
            var invitation = DomainGuard.Run(() => Invitation.Create(id, caller.TenantId, email,
                IInvitationTokens.Hash(tokens.Issue(id)), cmd.Roles.Select(r => (r.Role, r.WarehouseId)).ToList(), caller.Subject, s.Now));
            s.Db.Add(invitation);

            var dto = InvitationDto.From(invitation, s.Now);
            s.Audit(new AuditEntry("InvitationCreated", "Invitation", id, invitation.Version, null, null, dto));
            // The sender derives the token from the id at delivery; the payload never carries it (ONB-005).
            s.Publish("InvitationCreated", new { invitationId = id, tenantId = caller.TenantId, email, expiresAt = invitation.ExpiresAt });
            return dto;
        }, ct);
    }

    /// <summary>
    /// New invitations per tenant per rolling hour (ONB-007, DEC-044). The advisory lock is the first lock in the
    /// transaction, so concurrent invites by one tenant cannot pass the count together. Revoked and expired
    /// invitations still count: creation is the abused action. Nothing about other tenants or identities is read.
    /// </summary>
    private async Task EnforceInvitationLimitAsync(ITenantSession s, Guid tenantId, CancellationToken ct)
    {
        await s.Db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"invite:" + tenantId.ToString("D")}, 0))", ct);
        var since = s.Now - SaasOptions.InvitationWindow;
        var created = await s.Db.Set<Invitation>().AsNoTracking()
            .Where(i => i.CreatedAt > since).OrderBy(i => i.CreatedAt).Select(i => i.CreatedAt)
            .Take(saas.InvitationsPerTenantPerHour).ToListAsync(ct);
        if (created.Count >= saas.InvitationsPerTenantPerHour)
            throw AppException.RateLimited("Too many new invitations for this workspace. Retry later.",
                created[0] + SaasOptions.InvitationWindow - s.Now);
    }

    public Task<Page<InvitationDto>> ListAsync(InvitationStatus? status, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.TenantMembersManage);
        return uow.ReadAsync((s, c) =>
        {
            var query = s.Db.Set<Invitation>().AsNoTracking();
            var now = s.Now;
            query = status switch
            {
                InvitationStatus.Pending => query.Where(i => i.Status == InvitationStatus.Pending && i.ExpiresAt > now),
                InvitationStatus.Expired => query.Where(i => i.Status == InvitationStatus.Expired
                    || (i.Status == InvitationStatus.Pending && i.ExpiresAt <= now)),
                { } other => query.Where(i => i.Status == other),
                null => query,
            };
            return query.ToPageAsync(page, $"invitations|{status}", cursors, i => InvitationDto.From(i, now), c);
        }, ct);
    }

    public Task<InvitationDto> RevokeAsync(Guid id, int expectedVersion, ReasonRequest cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.TenantMembersManage);
        reasonValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            var invitation = await s.Db.Set<Invitation>().SingleOrDefaultAsync(i => i.Id == id, c) ?? throw AppException.NotFound("Invitation");
            Versioning.EnsureVersion(invitation.Version, expectedVersion);
            var before = InvitationDto.From(invitation, s.Now);
            DomainGuard.Run(() => invitation.Revoke(caller.Subject, s.Now));

            var after = InvitationDto.From(invitation, s.Now);
            s.Audit(new AuditEntry("InvitationRevoked", "Invitation", id, invitation.Version, cmd.Reason, before, after));
            return after;
        }, ct);
    }

    /// <summary>
    /// Accepts an invitation for the authenticated caller (no tenant header: the caller is not a member yet). The
    /// caller's verified email must equal the invitation's; the target tenant's lifecycle must allow team changes
    /// (team-admin category); a seat is reserved in the same transaction.
    /// </summary>
    public async Task<JoinedMembership> AcceptAsync(string subject, string? email, bool emailVerified, AcceptInvitation cmd,
        CancellationToken ct)
    {
        acceptValidator.ValidateOrThrow(cmd);
        if (!emailVerified || string.IsNullOrWhiteSpace(email))
            throw AppException.Forbidden("Verify your email address before accepting an invitation.");
        var hash = IInvitationTokens.Hash(cmd.Token);
        var tenantId = await lookup.FindTenantAsync(hash, ct) ?? throw NotValid();

        var state = await accessReader.ReadAsync(tenantId, ct) ?? throw NotValid();
        AccessGate.Enforce(state, clock.GetUtcNow(), AccessCategory.TeamAdmin, isOwner: false, feature: null);

        var joiner = new CallerContext(subject, ActorType.User, tenantId, TenantStatus.Active,
            new Dictionary<string, PermissionGrant>(), access: state);
        var normalizedEmail = email.Trim().ToLowerInvariant();

        return await uowFactory.For(joiner).WriteAsync(async (s, c) =>
        {
            // Lock the invitation first: two acceptances of one token serialise here (lock order: invitation →
            // usage counter → audit stream).
            var ids = await s.Db.Database
                .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.invitation WHERE token_hash = {hash} FOR UPDATE")
                .ToListAsync(c);
            if (ids.Count != 1)
                throw NotValid();
            var invitation = await s.Db.Set<Invitation>().SingleAsync(i => i.Id == ids[0], c);
            if (!invitation.IsUsable(s.Now))
                throw NotValid();
            if (invitation.EmailNormalized != normalizedEmail)
                throw AppException.Forbidden("This invitation was sent to a different email address.");

            var membership = await s.Db.Set<Membership>().SingleOrDefaultAsync(m => m.UserSubject == subject, c);
            if (membership is { Active: true })
                throw AppException.InvalidState("You are already a member of this workspace.");

            await quota.ReserveAsync(s, QuotaKey.ActiveSeats, 1, c);

            if (membership is null)
            {
                membership = Membership.Create(tenantId, subject, null, subject, s.Now);
                s.Db.Add(membership);
                s.Audit(new AuditEntry("MembershipCreated", "Membership", membership.Id, membership.Version, "Invitation accepted",
                    null, MembershipDto.From(membership)));
            }
            else
            {
                // A returning member starts from the invited roles only: grants from the earlier membership end.
                var before = MembershipDto.From(membership);
                membership.Reactivate(s.Now);
                foreach (var stale in await s.Db.Set<RoleGrant>().Where(g => g.MembershipId == membership.Id && g.RevokedAt == null).ToListAsync(c))
                    stale.Revoke(subject, s.Now);
                s.Audit(new AuditEntry("MembershipReactivated", "Membership", membership.Id, membership.Version, "Invitation accepted",
                    before, MembershipDto.From(membership)));
            }

            foreach (var (role, warehouseId) in invitation.Scopes.ToList())
            {
                if (warehouseId is { } wid && !await s.Db.Set<Warehouse>().AnyAsync(w => w.Id == wid && w.Active, c))
                    throw AppException.InvalidState("A warehouse named in the invitation has been retired. Ask for a new invitation.");
                RoleGrant grant;
                try
                {
                    // The inviter authorised the roles; SOD-3 still stops anyone inviting themselves into QA signing.
                    grant = DomainGuard.Run(() => RoleGrant.Create(tenantId, membership, role, warehouseId, "Invitation accepted",
                        invitation.InvitedBy, s.Now));
                }
                catch (SeparationOfDutyException ex)
                {
                    throw new AppException(ErrorCode.ApprovalSelf, ex.Message);
                }
                s.Db.Add(grant);
                s.Audit(new AuditEntry("RoleGranted", "RoleGrant", grant.Id, grant.Version, "Invitation accepted", null, RoleGrantDto.From(grant)));
            }

            var invitationBefore = InvitationDto.From(invitation, s.Now);
            invitation.Accept(subject, membership.Id, s.Now);
            s.Audit(new AuditEntry("InvitationAccepted", "Invitation", invitation.Id, invitation.Version, null, invitationBefore,
                InvitationDto.From(invitation, s.Now)));

            var tenant = await s.Db.Database.SqlQuery<TenantRow>(
                $"SELECT name AS \"Name\", slug AS \"Slug\", lifecycle::text AS \"Lifecycle\" FROM ti.tenant WHERE id = {tenantId}").SingleAsync(c);
            var planCode = await s.Db.Database.SqlQuery<string>($"""
                SELECT p.plan_code AS "Value" FROM cp.subscription sub JOIN cp.plan_version p ON p.id = sub.plan_version_id
                WHERE sub.tenant_id = {tenantId}
                """).FirstOrDefaultAsync(c);
            var scopes = invitation.Scopes;
            var permissions = CallerContext.GrantsFrom(scopes)
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new JoinedPermissionScope(p.Key, p.Value.AllWarehouses ? null : p.Value.Warehouses.Order().ToList()))
                .ToList();
            return new JoinedMembership(membership.Id, tenantId, tenant.Name, scopes.Select(x => new RoleScope(x.Role, x.WarehouseId)).ToList(),
                permissions, tenant.Slug ?? "", Enum.Parse<TenantLifecycle>(tenant.Lifecycle ?? nameof(TenantLifecycle.Ready)),
                state.Mode(s.Now), state.Reasons(s.Now), planCode);
        }, ct);
    }

    /// <summary>Lapsed Pending rows are marked Expired so the one-pending-per-address rule lets a new one through.</summary>
    private static async Task ExpireLapsedAsync(ITenantSession s, string email, CancellationToken ct) =>
        await s.Db.Database.ExecuteSqlAsync($"""
            UPDATE ti.invitation SET status = 'Expired', updated_at = now(), version = version + 1
            WHERE email_normalized = {email} AND status = 'Pending' AND expires_at <= now()
            """, ct);

    private sealed record TenantRow(string Name, string? Slug, string? Lifecycle);
}
