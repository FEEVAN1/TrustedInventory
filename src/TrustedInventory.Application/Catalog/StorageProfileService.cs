using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Catalog;

public sealed record ExcursionPolicyDto(bool ImmediateHold, string? Notes);

public sealed record StorageProfileDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt,
    string Name, decimal MinC, decimal MaxC, bool MonitoringRequired, int StaleAfterSeconds,
    ExcursionPolicyDto ExcursionPolicy, bool Active) : IVersioned
{
    public static StorageProfileDto From(StorageProfile p, ExcursionPolicy policy) =>
        new(p.Id, p.Version, p.CreatedAt, p.CreatedBy, p.UpdatedAt, p.Name, p.MinC, p.MaxC, p.MonitoringRequired,
            p.StaleAfterSeconds, new ExcursionPolicyDto(policy.HoldOnFirstReading, policy.Notes), p.Active);
}

public sealed record ExcursionPolicyInput(bool ImmediateHold, string? Notes);

public sealed record CreateStorageProfile(
    string Name, decimal MinC, decimal MaxC, bool MonitoringRequired, int? StaleAfterSeconds,
    ExcursionPolicyInput ExcursionPolicy, string Reason);

public sealed record UpdateStorageProfile(
    Optional<string> Name, Optional<decimal> MinC, Optional<decimal> MaxC, Optional<bool> MonitoringRequired,
    Optional<int> StaleAfterSeconds, Optional<ExcursionPolicyInput> ExcursionPolicy, string Reason);

public sealed record StorageProfileFilter(bool? Active);

public sealed class ExcursionPolicyInputValidator : AbstractValidator<ExcursionPolicyInput>
{
    public ExcursionPolicyInputValidator()
    {
        RuleFor(x => x.ImmediateHold).Equal(true).WithMessage("R1 supports only immediateHold: true (TEL-003).");
        RuleFor(x => x.Notes).MaximumLength(2000);
    }
}

public sealed class CreateStorageProfileValidator : AbstractValidator<CreateStorageProfile>
{
    public CreateStorageProfileValidator()
    {
        RuleFor(x => x.Name).ValidName();
        RuleFor(x => (decimal?)x.MinC).ValidCelsius().OverridePropertyName("minC");
        RuleFor(x => (decimal?)x.MaxC).ValidCelsius().OverridePropertyName("maxC");
        RuleFor(x => x.MaxC).GreaterThan(x => x.MinC).WithMessage("maxC must be greater than minC.");
        RuleFor(x => x.StaleAfterSeconds).InclusiveBetween(60, 86_400);
        RuleFor(x => x.ExcursionPolicy).NotNull().SetValidator(new ExcursionPolicyInputValidator());
        RuleFor(x => x.Reason).ValidReason();
    }
}

public sealed class UpdateStorageProfileValidator : AbstractValidator<UpdateStorageProfile>
{
    public UpdateStorageProfileValidator()
    {
        RuleFor(x => x.Reason).ValidReason();
        RuleFor(x => x).Must(x => x.Name.IsSet || x.MinC.IsSet || x.MaxC.IsSet || x.MonitoringRequired.IsSet
                || x.StaleAfterSeconds.IsSet || x.ExcursionPolicy.IsSet)
            .WithName("body").WithMessage("Change at least one field.");
        When(x => x.Name.IsSet, () => RuleFor(x => x.Name.Value).ValidName().OverridePropertyName("name"));
        When(x => x.MinC.IsSet, () => RuleFor(x => (decimal?)x.MinC.Value).ValidCelsius().OverridePropertyName("minC"));
        When(x => x.MaxC.IsSet, () => RuleFor(x => (decimal?)x.MaxC.Value).ValidCelsius().OverridePropertyName("maxC"));
        When(x => x.StaleAfterSeconds.IsSet, () =>
            RuleFor(x => x.StaleAfterSeconds.Value).InclusiveBetween(60, 86_400).OverridePropertyName("staleAfterSeconds"));
        When(x => x.ExcursionPolicy.IsSet, () =>
            RuleFor(x => x.ExcursionPolicy.Value).NotNull().SetValidator(new ExcursionPolicyInputValidator())
                .OverridePropertyName("excursionPolicy"));
    }
}

/// <summary>
/// Storage profiles and their excursion policy (CFG-002, DEC-027). Configuration changes are versioned and audited
/// with a reason (CFG-001); a profile used by active products cannot be retired.
/// </summary>
public sealed class StorageProfileService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IValidator<CreateStorageProfile> createValidator,
    IValidator<UpdateStorageProfile> updateValidator)
{
    public Task<StorageProfileDto> CreateAsync(CreateStorageProfile cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.TenantConfigManage);
        caller.RequireMutable();
        createValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            if (await s.Db.Set<StorageProfile>().AnyAsync(p => p.Name == cmd.Name, c))
                throw AppException.Validation("name", "A storage profile with this name already exists.");

            var (profile, policy) = DomainGuard.Run(() => StorageProfile.Create(caller.TenantId, cmd.Name, cmd.MinC, cmd.MaxC,
                cmd.MonitoringRequired, cmd.StaleAfterSeconds ?? 300, cmd.ExcursionPolicy.ImmediateHold, cmd.ExcursionPolicy.Notes,
                caller.Subject, s.Now));
            s.Db.AddRange(policy, profile);

            var dto = StorageProfileDto.From(profile, policy);
            s.Audit(new AuditEntry("StorageProfileCreated", "StorageProfile", profile.Id, profile.Version, cmd.Reason, null, dto));
            await s.Db.SaveChangesAsync(c);
            return dto;
        }, ct);
    }

    public Task<StorageProfileDto> UpdateAsync(Guid id, int expectedVersion, UpdateStorageProfile cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.TenantConfigManage);
        caller.RequireMutable();
        updateValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            var (profile, policy) = await Load(s, id, c);
            Versioning.EnsureVersion(profile.Version, expectedVersion);
            if (cmd.Name.IsSet && cmd.Name.Value != profile.Name
                && await s.Db.Set<StorageProfile>().AnyAsync(p => p.Name == cmd.Name.Value, c))
                throw AppException.Validation("name", "A storage profile with this name already exists.");
            var before = StorageProfileDto.From(profile, policy);

            DomainGuard.Run(() =>
            {
                profile.Update(cmd.Name.GetValueOrDefault(profile.Name), cmd.MinC.GetValueOrDefault(profile.MinC),
                    cmd.MaxC.GetValueOrDefault(profile.MaxC), cmd.MonitoringRequired.GetValueOrDefault(profile.MonitoringRequired),
                    cmd.StaleAfterSeconds.GetValueOrDefault(profile.StaleAfterSeconds), s.Now);
                if (cmd.ExcursionPolicy.IsSet)
                    policy.Update(cmd.ExcursionPolicy.Value.ImmediateHold, cmd.ExcursionPolicy.Value.Notes, s.Now);
            });

            var after = StorageProfileDto.From(profile, policy);
            s.Audit(new AuditEntry("StorageProfileUpdated", "StorageProfile", id, profile.Version, cmd.Reason, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    public Task<StorageProfileDto> RetireAsync(Guid id, int expectedVersion, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.TenantConfigManage);
        caller.RequireMutable();

        return uow.WriteAsync(async (s, c) =>
        {
            var (profile, policy) = await Load(s, id, c);
            Versioning.EnsureVersion(profile.Version, expectedVersion);
            if (await s.Db.Set<Product>().AnyAsync(p => p.Active && p.StorageProfileId == id, c))
                throw AppException.InvalidState("The storage profile is used by active products.");
            var before = StorageProfileDto.From(profile, policy);
            DomainGuard.Run(() =>
            {
                profile.Retire(s.Now);
                policy.Retire(s.Now);
            });

            var after = StorageProfileDto.From(profile, policy);
            s.Audit(new AuditEntry("StorageProfileRetired", "StorageProfile", id, profile.Version, null, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    public Task<StorageProfileDto> GetAsync(Guid id, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.CatalogRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var (profile, policy) = await Load(s, id, c);
            return StorageProfileDto.From(profile, policy);
        }, ct);
    }

    public Task<Page<StorageProfileDto>> ListAsync(StorageProfileFilter filter, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.CatalogRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var query = s.Db.Set<StorageProfile>().AsNoTracking();
            if (filter.Active is { } active)
                query = query.Where(p => p.Active == active);
            var profiles = await query.ToPageAsync(page, $"storage-profiles|{filter.Active}", cursors, p => p, c);

            var policyIds = profiles.Items.Select(p => p.ExcursionPolicyId).ToList();
            var policies = await s.Db.Set<ExcursionPolicy>().AsNoTracking().Where(p => policyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, c);
            return new Page<StorageProfileDto>(
                profiles.Items.Select(p => StorageProfileDto.From(p, policies[p.ExcursionPolicyId])).ToList(), profiles.NextCursor);
        }, ct);
    }

    private static async Task<(StorageProfile, ExcursionPolicy)> Load(ITenantSession s, Guid id, CancellationToken ct)
    {
        var profile = await s.Db.Set<StorageProfile>().SingleOrDefaultAsync(p => p.Id == id, ct)
            ?? throw AppException.NotFound("Storage profile");
        var policy = await s.Db.Set<ExcursionPolicy>().SingleAsync(p => p.Id == profile.ExcursionPolicyId, ct);
        return (profile, policy);
    }
}
