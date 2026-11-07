using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Locations;

public sealed record BinDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt,
    Guid WarehouseId, Guid ZoneId, string Code, BinKind Kind, int PickPriority,
    Quantity? Capacity, Guid? CapacityUomId, bool Active, bool CountLocked) : IVersioned
{
    // Count locks arrive with stock counts (M4); until then no bin can be locked.
    public static BinDto From(Bin b) =>
        new(b.Id, b.Version, b.CreatedAt, b.CreatedBy, b.UpdatedAt, b.WarehouseId, b.ZoneId, b.Code, b.Kind, b.PickPriority,
            b.Capacity is { } cap ? Quantity.From(cap) : null, b.CapacityUomId, b.Active, CountLocked: false);
}

public sealed record CreateBin(Guid ZoneId, string Code, BinKind Kind, int? PickPriority, Quantity? Capacity, Guid? CapacityUomId);

public sealed record UpdateBin(Optional<int> PickPriority, Optional<Quantity?> Capacity, Optional<Guid?> CapacityUomId);

public sealed record BinFilter(Guid? WarehouseId, Guid? ZoneId, bool? Active, BinKind? Kind, string? Search);

public sealed class CreateBinValidator : AbstractValidator<CreateBin>
{
    public CreateBinValidator()
    {
        RuleFor(x => x.ZoneId).NotEmpty();
        RuleFor(x => x.Code).ValidCode();
        RuleFor(x => x.Kind).Must(k => k is BinKind.Storage or BinKind.Staging or BinKind.Quarantine)
            .WithMessage("kind must be Storage, Staging or Quarantine.");
        RuleFor(x => x.PickPriority).InclusiveBetween(0, 100_000);
        RuleFor(x => x.Capacity).Must(c => c is null || Quantity.IsValidPositive(c.Value)).WithMessage(Quantity.PositiveMessage);
    }
}

public sealed class UpdateBinValidator : AbstractValidator<UpdateBin>
{
    public UpdateBinValidator()
    {
        RuleFor(x => x).Must(x => x.PickPriority.IsSet || x.Capacity.IsSet || x.CapacityUomId.IsSet)
            .WithName("body").WithMessage("Change at least one field.");
        RuleFor(x => x).Must(x => x.Capacity.IsSet == x.CapacityUomId.IsSet)
            .WithName("capacity").WithMessage("Send capacity and capacityUomId together.");
        When(x => x.PickPriority.IsSet, () => RuleFor(x => x.PickPriority.Value).InclusiveBetween(0, 100_000).OverridePropertyName("pickPriority"));
        When(x => x.Capacity.IsSet, () => RuleFor(x => x.Capacity.Value)
            .Must(c => c is null || Quantity.IsValidPositive(c.Value)).WithMessage(Quantity.PositiveMessage).OverridePropertyName("capacity"));
    }
}

/// <summary>Bins (section 07). Codes are unique per warehouse (DEC-023); Transit bins are system-managed (DEC-010).</summary>
public sealed class BinService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IValidator<CreateBin> createValidator,
    IValidator<UpdateBin> updateValidator)
{
    public Task<BinDto> CreateAsync(CreateBin cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.WarehousesManage);
        caller.RequireMutable();
        createValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            var zone = await s.Db.Set<Zone>().SingleOrDefaultAsync(z => z.Id == cmd.ZoneId, c) ?? throw AppException.NotFound("Zone");
            caller.RequireWarehouse(Permissions.WarehousesManage, zone.WarehouseId);
            if (zone.IsSystem || !zone.Active)
                throw AppException.InvalidState("Bins can only be added to active, non-system zones.");
            if (await s.Db.Set<Bin>().AnyAsync(b => b.WarehouseId == zone.WarehouseId && b.Code == cmd.Code, c))
                throw AppException.Validation("code", "A bin with this code already exists in the warehouse.");
            await EnsureActiveUnit(s, cmd.CapacityUomId, c);

            var bin = DomainGuard.Run(() => Bin.Create(caller.TenantId, zone.WarehouseId, zone.Id, cmd.Code, cmd.Kind,
                cmd.PickPriority ?? 1000, cmd.Capacity?.Value, cmd.CapacityUomId, caller.Subject, s.Now));
            s.Db.Add(bin);

            var dto = BinDto.From(bin);
            s.Audit(new AuditEntry("BinCreated", "Bin", bin.Id, bin.Version, null, null, dto));
            await s.Db.SaveChangesAsync(c);
            return dto;
        }, ct);
    }

    public Task<BinDto> UpdateAsync(Guid id, int expectedVersion, UpdateBin cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.WarehousesManage);
        caller.RequireMutable();
        updateValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            var bin = await Load(s, id, c);
            caller.RequireWarehouse(Permissions.WarehousesManage, bin.WarehouseId);
            Versioning.EnsureVersion(bin.Version, expectedVersion);
            if (cmd.CapacityUomId.IsSet)
                await EnsureActiveUnit(s, cmd.CapacityUomId.Value, c);
            var before = BinDto.From(bin);

            DomainGuard.Run(() => bin.Update(cmd.PickPriority.IsSet ? cmd.PickPriority.Value : null,
                cmd.Capacity.GetValueOrDefault(null)?.Value, cmd.CapacityUomId.GetValueOrDefault(null), cmd.Capacity.IsSet, s.Now));

            var after = BinDto.From(bin);
            s.Audit(new AuditEntry("BinUpdated", "Bin", id, bin.Version, null, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    public Task<BinDto> RetireAsync(Guid id, int expectedVersion, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.WarehousesManage);
        caller.RequireMutable();

        return uow.WriteAsync(async (s, c) =>
        {
            var bin = await Load(s, id, c);
            caller.RequireWarehouse(Permissions.WarehousesManage, bin.WarehouseId);
            Versioning.EnsureVersion(bin.Version, expectedVersion);
            var before = BinDto.From(bin);
            // M2 adds: a bin holding stock cannot be retired.
            DomainGuard.Run(() => bin.Retire(s.Now));

            var after = BinDto.From(bin);
            s.Audit(new AuditEntry("BinRetired", "Bin", id, bin.Version, null, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    public Task<BinDto> GetAsync(Guid id, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.CatalogRead);
        return uow.ReadAsync(async (s, c) => BinDto.From(await Load(s, id, c)), ct);
    }

    public Task<Page<BinDto>> ListAsync(BinFilter filter, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.CatalogRead);
        return uow.ReadAsync((s, c) =>
        {
            var query = s.Db.Set<Bin>().AsNoTracking();
            if (filter.WarehouseId is { } wid)
                query = query.Where(b => b.WarehouseId == wid);
            if (filter.ZoneId is { } zid)
                query = query.Where(b => b.ZoneId == zid);
            if (filter.Active is { } active)
                query = query.Where(b => b.Active == active);
            if (filter.Kind is { } kind)
                query = query.Where(b => b.Kind == kind);
            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var term = filter.Search.Trim().ToLowerInvariant();
                query = query.Where(b => b.Code.ToLower().Contains(term));
            }
            return query.ToPageAsync(page,
                $"bins|{filter.WarehouseId}|{filter.ZoneId}|{filter.Active}|{filter.Kind}|{filter.Search}", cursors, BinDto.From, c);
        }, ct);
    }

    private static async Task<Bin> Load(ITenantSession s, Guid id, CancellationToken ct) =>
        await s.Db.Set<Bin>().SingleOrDefaultAsync(b => b.Id == id, ct) ?? throw AppException.NotFound("Bin");

    private static async Task EnsureActiveUnit(ITenantSession s, Guid? unitId, CancellationToken ct)
    {
        if (unitId is { } uid && !await s.Db.Set<Unit>().AnyAsync(u => u.Id == uid && u.Active, ct))
            throw AppException.Validation("capacityUomId", "Unknown or retired unit.");
    }
}
