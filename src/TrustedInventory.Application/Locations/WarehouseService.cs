using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Saas;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Locations;

public sealed record WarehouseDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt,
    string Code, string Name, string TimeZone, bool Active) : IVersioned
{
    public static WarehouseDto From(Warehouse w) =>
        new(w.Id, w.Version, w.CreatedAt, w.CreatedBy, w.UpdatedAt, w.Code, w.Name, w.TimeZone, w.Active);
}

public sealed record CreateWarehouse(string Code, string Name, string TimeZone);

public sealed record UpdateWarehouse(Optional<string> Name, Optional<string> TimeZone, string Reason);

public sealed record WarehouseFilter(bool? Active, string? Search);

public sealed class CreateWarehouseValidator : AbstractValidator<CreateWarehouse>
{
    public CreateWarehouseValidator()
    {
        RuleFor(x => x.Code).ValidCode();
        RuleFor(x => x.Name).ValidName();
        RuleFor(x => x.TimeZone).ValidTimeZone();
    }
}

public sealed class UpdateWarehouseValidator : AbstractValidator<UpdateWarehouse>
{
    public UpdateWarehouseValidator()
    {
        RuleFor(x => x.Reason).ValidReason();
        RuleFor(x => x).Must(x => x.Name.IsSet || x.TimeZone.IsSet).WithName("body").WithMessage("Change at least one field.");
        When(x => x.Name.IsSet, () => RuleFor(x => x.Name.Value).ValidName().OverridePropertyName("name"));
        When(x => x.TimeZone.IsSet, () => RuleFor(x => x.TimeZone.Value).ValidTimeZone().OverridePropertyName("timeZone"));
    }
}

/// <summary>
/// Warehouses (section 07). Creating one also creates its system zone and Transit bin (DEC-010). Active
/// warehouses count against the plan's active_warehouses quota (x-quota, DEC-045).
/// </summary>
public sealed class WarehouseService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IQuotaGate quota,
    IValidator<CreateWarehouse> createValidator,
    IValidator<UpdateWarehouse> updateValidator)
{
    public Task<WarehouseDto> CreateAsync(CreateWarehouse cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.RequireTenantWide(Permissions.WarehousesManage);
        caller.RequireMutable();
        createValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            // SAS-005 order: reserve the quota, then domain validation; a rejection rolls the reservation back.
            await quota.ReserveAsync(s, QuotaKey.ActiveWarehouses, 1, c);
            if (await s.Db.Set<Warehouse>().AnyAsync(w => w.Code == cmd.Code, c))
                throw AppException.Validation("code", "A warehouse with this code already exists.");

            var warehouse = Warehouse.Create(caller.TenantId, cmd.Code, cmd.Name, cmd.TimeZone, caller.Subject, s.Now);
            var systemZone = Zone.CreateSystem(caller.TenantId, warehouse.Id, caller.Subject, s.Now);
            var transit = Bin.CreateTransit(caller.TenantId, warehouse.Id, systemZone.Id, caller.Subject, s.Now);
            s.Db.AddRange(warehouse, systemZone, transit);

            var dto = WarehouseDto.From(warehouse);
            s.Audit(new AuditEntry("WarehouseCreated", "Warehouse", warehouse.Id, warehouse.Version, null, null, dto));
            s.Publish("WarehouseCreated", new { warehouseId = warehouse.Id, warehouse.Code });
            await s.Db.SaveChangesAsync(c);
            return dto;
        }, ct);
    }

    public Task<WarehouseDto> UpdateAsync(Guid id, int expectedVersion, UpdateWarehouse cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.RequireWarehouse(Permissions.WarehousesManage, id);
        caller.RequireMutable();
        updateValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            var warehouse = await Load(s, id, c);
            Versioning.EnsureVersion(warehouse.Version, expectedVersion);
            var before = WarehouseDto.From(warehouse);
            DomainGuard.Run(() => warehouse.Update(cmd.Name.GetValueOrDefault(warehouse.Name), cmd.TimeZone.GetValueOrDefault(warehouse.TimeZone), s.Now));

            var after = WarehouseDto.From(warehouse);
            s.Audit(new AuditEntry("WarehouseUpdated", "Warehouse", id, warehouse.Version, cmd.Reason, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    public Task<WarehouseDto> RetireAsync(Guid id, int expectedVersion, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.RequireWarehouse(Permissions.WarehousesManage, id);
        caller.RequireMutable();

        return uow.WriteAsync(async (s, c) =>
        {
            var warehouse = await Load(s, id, c);
            Versioning.EnsureVersion(warehouse.Version, expectedVersion);
            var before = WarehouseDto.From(warehouse);
            DomainGuard.Run(() => warehouse.Retire(s.Now));
            // Before the flush: a counter seeded now still counts this warehouse once, then releases it (DEC-045).
            await quota.ReleaseAsync(s, QuotaKey.ActiveWarehouses, 1, c);

            var after = WarehouseDto.From(warehouse);
            s.Audit(new AuditEntry("WarehouseRetired", "Warehouse", id, warehouse.Version, null, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    public Task<WarehouseDto> GetAsync(Guid id, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.CatalogRead);
        return uow.ReadAsync(async (s, c) => WarehouseDto.From(await Load(s, id, c)), ct);
    }

    public Task<Page<WarehouseDto>> ListAsync(WarehouseFilter filter, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.CatalogRead);
        return uow.ReadAsync((s, c) =>
        {
            var query = s.Db.Set<Warehouse>().AsNoTracking();
            if (filter.Active is { } active)
                query = query.Where(w => w.Active == active);
            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var term = filter.Search.Trim().ToLowerInvariant();
                query = query.Where(w => w.Code.ToLower().Contains(term) || w.Name.ToLower().Contains(term));
            }
            return query.ToPageAsync(page, $"warehouses|{filter.Active}|{filter.Search}", cursors, WarehouseDto.From, c);
        }, ct);
    }

    private static async Task<Warehouse> Load(ITenantSession s, Guid id, CancellationToken ct) =>
        await s.Db.Set<Warehouse>().SingleOrDefaultAsync(w => w.Id == id, ct) ?? throw AppException.NotFound("Warehouse");
}
