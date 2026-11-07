using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Catalog;

public sealed record PartnerDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt,
    string Code, string Name, PartnerType Type, bool Active) : IVersioned
{
    public static PartnerDto From(Partner p) =>
        new(p.Id, p.Version, p.CreatedAt, p.CreatedBy, p.UpdatedAt, p.Code, p.Name, p.Type, p.Active);
}

public sealed record CreatePartner(string Code, string Name, PartnerType Type);

public sealed record UpdatePartner(Optional<string> Name, Optional<PartnerType> Type);

public sealed record PartnerFilter(bool? Active, string? Search, PartnerType? Type);

public sealed class CreatePartnerValidator : AbstractValidator<CreatePartner>
{
    public CreatePartnerValidator()
    {
        RuleFor(x => x.Code).ValidCode();
        RuleFor(x => x.Name).ValidName();
        RuleFor(x => x.Type).IsInEnum();
    }
}

public sealed class UpdatePartnerValidator : AbstractValidator<UpdatePartner>
{
    public UpdatePartnerValidator()
    {
        RuleFor(x => x).Must(x => x.Name.IsSet || x.Type.IsSet).WithName("body").WithMessage("Change at least one field.");
        When(x => x.Name.IsSet, () => RuleFor(x => x.Name.Value).ValidName().OverridePropertyName("name"));
        When(x => x.Type.IsSet, () => RuleFor(x => x.Type.Value).IsInEnum().OverridePropertyName("type"));
    }
}

/// <summary>Suppliers and customers (section 07). Operational reference data only.</summary>
public sealed class PartnerService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IValidator<CreatePartner> createValidator,
    IValidator<UpdatePartner> updateValidator)
{
    public Task<PartnerDto> CreateAsync(CreatePartner cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.CatalogManage);
        caller.RequireMutable();
        createValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            if (await s.Db.Set<Partner>().AnyAsync(p => p.Code == cmd.Code, c))
                throw AppException.Validation("code", "A partner with this code already exists.");

            var partner = Partner.Create(caller.TenantId, cmd.Code, cmd.Name, cmd.Type, caller.Subject, s.Now);
            s.Db.Add(partner);
            var dto = PartnerDto.From(partner);
            s.Audit(new AuditEntry("PartnerCreated", "Partner", partner.Id, partner.Version, null, null, dto));
            await s.Db.SaveChangesAsync(c);
            return dto;
        }, ct);
    }

    public Task<PartnerDto> UpdateAsync(Guid id, int expectedVersion, UpdatePartner cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.CatalogManage);
        caller.RequireMutable();
        updateValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            var partner = await Load(s, id, c);
            Versioning.EnsureVersion(partner.Version, expectedVersion);
            var before = PartnerDto.From(partner);
            DomainGuard.Run(() => partner.Update(cmd.Name.GetValueOrDefault(partner.Name), cmd.Type.GetValueOrDefault(partner.Type), s.Now));

            var after = PartnerDto.From(partner);
            s.Audit(new AuditEntry("PartnerUpdated", "Partner", id, partner.Version, null, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    public Task<PartnerDto> RetireAsync(Guid id, int expectedVersion, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.CatalogManage);
        caller.RequireMutable();

        return uow.WriteAsync(async (s, c) =>
        {
            var partner = await Load(s, id, c);
            Versioning.EnsureVersion(partner.Version, expectedVersion);
            var before = PartnerDto.From(partner);
            DomainGuard.Run(() => partner.Retire(s.Now));

            var after = PartnerDto.From(partner);
            s.Audit(new AuditEntry("PartnerRetired", "Partner", id, partner.Version, null, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    public Task<PartnerDto> GetAsync(Guid id, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.CatalogRead);
        return uow.ReadAsync(async (s, c) => PartnerDto.From(await Load(s, id, c)), ct);
    }

    public Task<Page<PartnerDto>> ListAsync(PartnerFilter filter, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.CatalogRead);
        return uow.ReadAsync((s, c) =>
        {
            var query = s.Db.Set<Partner>().AsNoTracking();
            if (filter.Active is { } active)
                query = query.Where(p => p.Active == active);
            if (filter.Type is { } type)
                query = query.Where(p => p.Type == type);
            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var term = filter.Search.Trim().ToLowerInvariant();
                query = query.Where(p => p.Code.ToLower().Contains(term) || p.Name.ToLower().Contains(term));
            }
            return query.ToPageAsync(page, $"partners|{filter.Active}|{filter.Type}|{filter.Search}", cursors, PartnerDto.From, c);
        }, ct);
    }

    private static async Task<Partner> Load(ITenantSession s, Guid id, CancellationToken ct) =>
        await s.Db.Set<Partner>().SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw AppException.NotFound("Partner");
}
