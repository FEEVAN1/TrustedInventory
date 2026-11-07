using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Catalog;

public sealed record UnitDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt,
    string Code, string Name, bool Active) : IVersioned
{
    public static UnitDto From(Unit u) => new(u.Id, u.Version, u.CreatedAt, u.CreatedBy, u.UpdatedAt, u.Code, u.Name, u.Active);
}

public sealed record CreateUnit(string Code, string Name);

public sealed record UpdateUnit(Optional<string> Name);

public sealed record UnitFilter(bool? Active, string? Search);

public sealed class CreateUnitValidator : AbstractValidator<CreateUnit>
{
    public CreateUnitValidator()
    {
        RuleFor(x => x.Code).ValidCode();
        RuleFor(x => x.Name).ValidName();
    }
}

public sealed class UpdateUnitValidator : AbstractValidator<UpdateUnit>
{
    public UpdateUnitValidator()
    {
        RuleFor(x => x).Must(x => x.Name.IsSet).WithName("body").WithMessage("Change at least one field.");
        When(x => x.Name.IsSet, () => RuleFor(x => x.Name.Value).ValidName().OverridePropertyName("name"));
    }
}

/// <summary>Units of measure (section 07). A unit still used by active products or conversions cannot be retired.</summary>
public sealed class UnitService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IValidator<CreateUnit> createValidator,
    IValidator<UpdateUnit> updateValidator)
{
    public Task<UnitDto> CreateAsync(CreateUnit cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.CatalogManage);
        caller.RequireMutable();
        createValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            if (await s.Db.Set<Unit>().AnyAsync(u => u.Code == cmd.Code, c))
                throw AppException.Validation("code", "A unit with this code already exists.");

            var unit = Unit.Create(caller.TenantId, cmd.Code, cmd.Name, caller.Subject, s.Now);
            s.Db.Add(unit);
            var dto = UnitDto.From(unit);
            s.Audit(new AuditEntry("UnitCreated", "Unit", unit.Id, unit.Version, null, null, dto));
            await s.Db.SaveChangesAsync(c);
            return dto;
        }, ct);
    }

    public Task<UnitDto> UpdateAsync(Guid id, int expectedVersion, UpdateUnit cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.CatalogManage);
        caller.RequireMutable();
        updateValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            var unit = await Load(s, id, c);
            Versioning.EnsureVersion(unit.Version, expectedVersion);
            var before = UnitDto.From(unit);
            DomainGuard.Run(() => unit.Rename(cmd.Name.Value, s.Now));

            var after = UnitDto.From(unit);
            s.Audit(new AuditEntry("UnitUpdated", "Unit", id, unit.Version, null, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    public Task<UnitDto> RetireAsync(Guid id, int expectedVersion, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.CatalogManage);
        caller.RequireMutable();

        return uow.WriteAsync(async (s, c) =>
        {
            var unit = await Load(s, id, c);
            Versioning.EnsureVersion(unit.Version, expectedVersion);
            if (await s.Db.Set<Product>().AnyAsync(p => p.Active && p.BaseUomId == id, c)
                || await s.Db.Set<ProductConversion>().AnyAsync(pc => pc.Active && pc.AlternateUomId == id, c))
                throw AppException.InvalidState("The unit is used by active products or conversions.");
            var before = UnitDto.From(unit);
            DomainGuard.Run(() => unit.Retire(s.Now));

            var after = UnitDto.From(unit);
            s.Audit(new AuditEntry("UnitRetired", "Unit", id, unit.Version, null, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    public Task<UnitDto> GetAsync(Guid id, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.CatalogRead);
        return uow.ReadAsync(async (s, c) => UnitDto.From(await Load(s, id, c)), ct);
    }

    public Task<Page<UnitDto>> ListAsync(UnitFilter filter, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.CatalogRead);
        return uow.ReadAsync((s, c) =>
        {
            var query = s.Db.Set<Unit>().AsNoTracking();
            if (filter.Active is { } active)
                query = query.Where(u => u.Active == active);
            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var term = filter.Search.Trim().ToLowerInvariant();
                query = query.Where(u => u.Code.ToLower().Contains(term) || u.Name.ToLower().Contains(term));
            }
            return query.ToPageAsync(page, $"units|{filter.Active}|{filter.Search}", cursors, UnitDto.From, c);
        }, ct);
    }

    private static async Task<Unit> Load(ITenantSession s, Guid id, CancellationToken ct) =>
        await s.Db.Set<Unit>().SingleOrDefaultAsync(u => u.Id == id, ct) ?? throw AppException.NotFound("Unit");
}
