using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Locations;

public sealed record ZoneDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt,
    Guid WarehouseId, string Code, string Name, decimal? TempMinC, decimal? TempMaxC,
    IReadOnlyList<string> AllowedHazardClasses, bool System, bool Active) : IVersioned
{
    public static ZoneDto From(Zone z, IEnumerable<string> hazardClasses) =>
        new(z.Id, z.Version, z.CreatedAt, z.CreatedBy, z.UpdatedAt, z.WarehouseId, z.Code, z.Name, z.TempMinC, z.TempMaxC,
            hazardClasses.Order(StringComparer.Ordinal).ToList(), z.IsSystem, z.Active);
}

public sealed record CreateZone(
    Guid WarehouseId, string Code, string Name, decimal? TempMinC, decimal? TempMaxC, IReadOnlyList<string>? AllowedHazardClasses);

public sealed record UpdateZone(
    Optional<string> Name, Optional<decimal?> TempMinC, Optional<decimal?> TempMaxC,
    Optional<IReadOnlyList<string>> AllowedHazardClasses, string Reason);

public sealed record ZoneFilter(Guid? WarehouseId, bool? Active);

public sealed class CreateZoneValidator : AbstractValidator<CreateZone>
{
    public CreateZoneValidator()
    {
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.Code).ValidCode();
        RuleFor(x => x.Name).ValidName();
        RuleFor(x => x.TempMinC).ValidCelsius();
        RuleFor(x => x.TempMaxC).ValidCelsius();
        RuleFor(x => x.AllowedHazardClasses).Must(BeDistinct).WithMessage("Hazard classes must be unique.")
            .Must(l => l is null || l.Count <= 50).WithMessage("At most 50 hazard classes.");
        RuleForEach(x => x.AllowedHazardClasses).ValidCode();
    }

    internal static bool BeDistinct(IReadOnlyList<string>? list) => list is null || list.Distinct(StringComparer.Ordinal).Count() == list.Count;
}

public sealed class UpdateZoneValidator : AbstractValidator<UpdateZone>
{
    public UpdateZoneValidator()
    {
        RuleFor(x => x.Reason).ValidReason();
        RuleFor(x => x).Must(x => x.Name.IsSet || x.TempMinC.IsSet || x.TempMaxC.IsSet || x.AllowedHazardClasses.IsSet)
            .WithName("body").WithMessage("Change at least one field.");
        RuleFor(x => x).Must(x => x.TempMinC.IsSet == x.TempMaxC.IsSet)
            .WithName("tempMinC").WithMessage("Send tempMinC and tempMaxC together.");
        When(x => x.Name.IsSet, () => RuleFor(x => x.Name.Value).ValidName().OverridePropertyName("name"));
        When(x => x.TempMinC.IsSet, () => RuleFor(x => x.TempMinC.Value).ValidCelsius().OverridePropertyName("tempMinC"));
        When(x => x.TempMaxC.IsSet, () => RuleFor(x => x.TempMaxC.Value).ValidCelsius().OverridePropertyName("tempMaxC"));
        When(x => x.AllowedHazardClasses.IsSet, () =>
        {
            RuleFor(x => x.AllowedHazardClasses.Value).NotNull().Must(CreateZoneValidator.BeDistinct)
                .Must(l => l.Count <= 50).OverridePropertyName("allowedHazardClasses");
            RuleForEach(x => x.AllowedHazardClasses.Value).ValidCode().OverridePropertyName("allowedHazardClasses");
        });
    }
}

/// <summary>Zones carry the temperature range and allowed hazard classes used for compatibility (DEC-012).</summary>
public sealed class ZoneService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IValidator<CreateZone> createValidator,
    IValidator<UpdateZone> updateValidator)
{
    public Task<ZoneDto> CreateAsync(CreateZone cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.WarehousesManage);
        caller.RequireMutable();
        createValidator.ValidateOrThrow(cmd);
        caller.RequireWarehouse(Permissions.WarehousesManage, cmd.WarehouseId);

        return uow.WriteAsync(async (s, c) =>
        {
            var warehouse = await s.Db.Set<Warehouse>().SingleOrDefaultAsync(w => w.Id == cmd.WarehouseId, c)
                ?? throw AppException.NotFound("Warehouse");
            if (!warehouse.Active)
                throw AppException.InvalidState("Warehouse is retired.");
            if (await s.Db.Set<Zone>().AnyAsync(z => z.WarehouseId == cmd.WarehouseId && z.Code == cmd.Code, c))
                throw AppException.Validation("code", "A zone with this code already exists in the warehouse.");

            var zone = DomainGuard.Run(() => Zone.Create(caller.TenantId, cmd.WarehouseId, cmd.Code, cmd.Name,
                cmd.TempMinC, cmd.TempMaxC, caller.Subject, s.Now));
            var hazards = cmd.AllowedHazardClasses ?? [];
            s.Db.Add(zone);
            s.Db.AddRange(hazards.Select(h => ZoneAllowedHazardClass.Create(caller.TenantId, zone.Id, h, caller.Subject, s.Now)));

            var dto = ZoneDto.From(zone, hazards);
            s.Audit(new AuditEntry("ZoneCreated", "Zone", zone.Id, zone.Version, null, null, dto));
            await s.Db.SaveChangesAsync(c);
            return dto;
        }, ct);
    }

    public Task<ZoneDto> UpdateAsync(Guid id, int expectedVersion, UpdateZone cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.WarehousesManage);
        caller.RequireMutable();
        updateValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            var zone = await Load(s, id, c);
            caller.RequireWarehouse(Permissions.WarehousesManage, zone.WarehouseId);
            Versioning.EnsureVersion(zone.Version, expectedVersion);
            var hazardRows = await s.Db.Set<ZoneAllowedHazardClass>().Where(h => h.ZoneId == id).ToListAsync(c);
            var before = ZoneDto.From(zone, hazardRows.Select(h => h.HazardClass));

            DomainGuard.Run(() => zone.Update(cmd.Name.GetValueOrDefault(zone.Name), cmd.TempMinC.GetValueOrDefault(null),
                cmd.TempMaxC.GetValueOrDefault(null), cmd.TempMinC.IsSet, s.Now));

            var hazards = before.AllowedHazardClasses;
            if (cmd.AllowedHazardClasses.IsSet)
            {
                // The allowed list is replaced as a whole; rows are removed and re-added in this transaction.
                hazards = cmd.AllowedHazardClasses.Value;
                s.Db.RemoveRange(hazardRows.Where(r => !hazards.Contains(r.HazardClass)));
                s.Db.AddRange(hazards.Where(h => hazardRows.All(r => r.HazardClass != h))
                    .Select(h => ZoneAllowedHazardClass.Create(caller.TenantId, id, h, caller.Subject, s.Now)));
            }

            var after = ZoneDto.From(zone, hazards);
            s.Audit(new AuditEntry("ZoneUpdated", "Zone", id, zone.Version, cmd.Reason, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    public Task<ZoneDto> RetireAsync(Guid id, int expectedVersion, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.WarehousesManage);
        caller.RequireMutable();

        return uow.WriteAsync(async (s, c) =>
        {
            var zone = await Load(s, id, c);
            caller.RequireWarehouse(Permissions.WarehousesManage, zone.WarehouseId);
            Versioning.EnsureVersion(zone.Version, expectedVersion);
            var hazards = await HazardsOf(s, id, c);
            var before = ZoneDto.From(zone, hazards);
            DomainGuard.Run(() => zone.Retire(s.Now));

            var after = ZoneDto.From(zone, hazards);
            s.Audit(new AuditEntry("ZoneRetired", "Zone", id, zone.Version, null, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    public Task<ZoneDto> GetAsync(Guid id, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.CatalogRead);
        return uow.ReadAsync(async (s, c) => ZoneDto.From(await Load(s, id, c), await HazardsOf(s, id, c)), ct);
    }

    public Task<Page<ZoneDto>> ListAsync(ZoneFilter filter, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.CatalogRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var query = s.Db.Set<Zone>().AsNoTracking();
            if (filter.WarehouseId is { } wid)
                query = query.Where(z => z.WarehouseId == wid);
            if (filter.Active is { } active)
                query = query.Where(z => z.Active == active);
            var zones = await query.ToPageAsync(page, $"zones|{filter.WarehouseId}|{filter.Active}", cursors, z => z, c);

            var ids = zones.Items.Select(z => z.Id).ToList();
            var hazards = (await s.Db.Set<ZoneAllowedHazardClass>().AsNoTracking().Where(h => ids.Contains(h.ZoneId)).ToListAsync(c))
                .ToLookup(h => h.ZoneId, h => h.HazardClass);
            return new Page<ZoneDto>(zones.Items.Select(z => ZoneDto.From(z, hazards[z.Id])).ToList(), zones.NextCursor);
        }, ct);
    }

    private static async Task<Zone> Load(ITenantSession s, Guid id, CancellationToken ct) =>
        await s.Db.Set<Zone>().SingleOrDefaultAsync(z => z.Id == id, ct) ?? throw AppException.NotFound("Zone");

    private static async Task<List<string>> HazardsOf(ITenantSession s, Guid zoneId, CancellationToken ct) =>
        await s.Db.Set<ZoneAllowedHazardClass>().Where(h => h.ZoneId == zoneId).Select(h => h.HazardClass).ToListAsync(ct);
}
