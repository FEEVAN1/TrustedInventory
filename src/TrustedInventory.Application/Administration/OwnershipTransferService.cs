using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Administration;

public sealed record OwnershipTransferDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt,
    string FromSubject, Guid ToMembershipId, OwnershipTransferStatus Status, DateTimeOffset ExpiresAt,
    DateTimeOffset? AcceptedAt) : IVersioned
{
    public static OwnershipTransferDto From(OwnershipTransfer t, DateTimeOffset now) =>
        new(t.Id, t.Version, t.CreatedAt, t.CreatedBy, t.UpdatedAt, t.FromSubject, t.ToMembershipId, t.EffectiveStatus(now),
            t.ExpiresAt, t.AcceptedAt);
}

public sealed record CreateOwnershipTransfer(Guid ToMembershipId, string Reason, string ReauthToken);

public sealed class CreateOwnershipTransferValidator : AbstractValidator<CreateOwnershipTransfer>
{
    public CreateOwnershipTransferValidator()
    {
        RuleFor(x => x.ToMembershipId).NotEmpty();
        RuleFor(x => x.Reason).ValidReason();
        RuleFor(x => x.ReauthToken).NotEmpty().MaximumLength(16_384);
    }
}

/// <summary>
/// Ownership transfer (ONB-006, SOD-5, SOD-7). The owner starts it after re-authenticating; the recipient's
/// acceptance moves the TenantOwner grant in one transaction, so the tenant always has exactly the owners it
/// had before plus or minus this one move, and never zero.
/// </summary>
public sealed class OwnershipTransferService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    IReauthVerifier reauth,
    IValidator<CreateOwnershipTransfer> createValidator)
{
    public async Task<OwnershipTransferDto> CreateAsync(CreateOwnershipTransfer cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.TenantOwnershipTransfer);
        createValidator.ValidateOrThrow(cmd);
        if (!caller.IsOwner)
            throw AppException.Forbidden("Only the workspace owner can transfer ownership.");
        var reauthReference = await reauth.VerifyAsync(cmd.ReauthToken, caller.Subject, ct);

        return await uow.WriteAsync(async (s, c) =>
        {
            var pending = await s.Db.Set<OwnershipTransfer>().Where(t => t.Status == OwnershipTransferStatus.Pending).ToListAsync(c);
            foreach (var lapsed in pending.Where(t => t.EffectiveStatus(s.Now) == OwnershipTransferStatus.Expired))
                lapsed.Expire(s.Now);
            if (pending.Any(t => t.EffectiveStatus(s.Now) == OwnershipTransferStatus.Pending))
                throw AppException.InvalidState("An ownership transfer is already pending. Cancel it first.");

            var recipient = await s.Db.Set<Membership>().SingleOrDefaultAsync(m => m.Id == cmd.ToMembershipId, c)
                ?? throw AppException.NotFound("Membership");
            var transfer = DomainGuard.Run(() => OwnershipTransfer.Start(caller.TenantId, caller.Subject, recipient, reauthReference, s.Now));
            s.Db.Add(transfer);

            var dto = OwnershipTransferDto.From(transfer, s.Now);
            s.Audit(new AuditEntry("OwnershipTransferStarted", "OwnershipTransfer", transfer.Id, transfer.Version, cmd.Reason, null, dto));
            s.Publish("OwnershipTransferStarted", new { transferId = transfer.Id, toMembershipId = recipient.Id });
            return dto;
        }, ct);
    }

    /// <summary>The named recipient accepts: the old owner's TenantOwner grant ends and the recipient's begins.</summary>
    public Task<OwnershipTransferDto> AcceptAsync(Guid id, int expectedVersion, CancellationToken ct)
    {
        var caller = callers.Caller;
        return uow.WriteAsync(async (s, c) =>
        {
            var transfer = await Load(s, id, c);
            // Members other than the recipient cannot act on (or learn more about) the transfer.
            if (transfer.ToSubject != caller.Subject)
                throw AppException.Forbidden("Only the named recipient can accept this transfer.");
            Versioning.EnsureVersion(transfer.Version, expectedVersion);
            var before = OwnershipTransferDto.From(transfer, s.Now);
            DomainGuard.Run(() => transfer.Accept(caller.Subject, s.Now));

            var recipient = await s.Db.Set<Membership>().SingleAsync(m => m.Id == transfer.ToMembershipId, c);
            if (!recipient.Active)
                throw AppException.InvalidState("Your membership is no longer active.");
            var fromOwnerGrants = await s.Db.Set<RoleGrant>()
                .Where(g => g.GranteeSubject == transfer.FromSubject && g.Role == RoleName.TenantOwner && g.RevokedAt == null)
                .ToListAsync(c);
            if (fromOwnerGrants.Count == 0)
                throw AppException.InvalidState("The person who started this transfer no longer owns the workspace.");

            foreach (var grant in fromOwnerGrants)
            {
                var grantBefore = RoleGrantDto.From(grant);
                grant.Revoke(caller.Subject, s.Now);
                s.Audit(new AuditEntry("RoleRevoked", "RoleGrant", grant.Id, grant.Version, "Ownership transferred", grantBefore,
                    RoleGrantDto.From(grant)));
            }
            if (!await s.Db.Set<RoleGrant>().AnyAsync(g => g.MembershipId == recipient.Id && g.Role == RoleName.TenantOwner
                    && g.RevokedAt == null, c))
            {
                var owner = DomainGuard.Run(() => RoleGrant.CreateOwner(caller.TenantId, recipient, "Ownership transfer accepted",
                    transfer.FromSubject, s.Now));
                s.Db.Add(owner);
                s.Audit(new AuditEntry("RoleGranted", "RoleGrant", owner.Id, owner.Version, "Ownership transfer accepted", null,
                    RoleGrantDto.From(owner)));
            }

            var after = OwnershipTransferDto.From(transfer, s.Now);
            s.Audit(new AuditEntry("OwnershipTransferAccepted", "OwnershipTransfer", id, transfer.Version, null, before, after));
            return after;
        }, ct);
    }

    public Task<OwnershipTransferDto> CancelAsync(Guid id, int expectedVersion, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.TenantOwnershipTransfer);
        return uow.WriteAsync(async (s, c) =>
        {
            var transfer = await Load(s, id, c);
            Versioning.EnsureVersion(transfer.Version, expectedVersion);
            var before = OwnershipTransferDto.From(transfer, s.Now);
            DomainGuard.Run(() => transfer.Cancel(s.Now));

            var after = OwnershipTransferDto.From(transfer, s.Now);
            s.Audit(new AuditEntry("OwnershipTransferCancelled", "OwnershipTransfer", id, transfer.Version, null, before, after));
            return after;
        }, ct);
    }

    private static async Task<OwnershipTransfer> Load(ITenantSession s, Guid id, CancellationToken ct) =>
        await s.Db.Set<OwnershipTransfer>().SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw AppException.NotFound("Ownership transfer");
}
