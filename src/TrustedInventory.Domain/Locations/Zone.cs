using TrustedInventory.Domain.Common;

namespace TrustedInventory.Domain.Locations;

/// <summary>
/// Storage area with an operating temperature range (null pair = uncontrolled ambient) and an explicit
/// allowed hazard-class list (DEC-012). The system zone holds the warehouse Transit bin (DEC-010).
/// </summary>
public sealed class Zone : TenantEntity
{
    public const string SystemZoneCode = "SYS";

    private Zone() { }

    public Guid WarehouseId { get; private set; }
    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public decimal? TempMinC { get; private set; }
    public decimal? TempMaxC { get; private set; }
    public bool IsSystem { get; private set; }
    public bool Active { get; private set; }

    public static Zone Create(Guid tenantId, Guid warehouseId, string code, string name, decimal? tempMinC,
        decimal? tempMaxC, string actor, DateTimeOffset now)
    {
        ValidateRange(tempMinC, tempMaxC);
        var zone = new Zone
        {
            WarehouseId = warehouseId, Code = code, Name = name, TempMinC = tempMinC, TempMaxC = tempMaxC, Active = true,
        };
        zone.Stamp(tenantId, actor, now);
        return zone;
    }

    public static Zone CreateSystem(Guid tenantId, Guid warehouseId, string actor, DateTimeOffset now)
    {
        var zone = Create(tenantId, warehouseId, SystemZoneCode, "System", null, null, actor, now);
        zone.IsSystem = true;
        return zone;
    }

    public void Update(string? name, decimal? tempMinC, decimal? tempMaxC, bool rangeSpecified, DateTimeOffset now)
    {
        EnsureEditable();
        if (rangeSpecified)
        {
            ValidateRange(tempMinC, tempMaxC);
            TempMinC = tempMinC;
            TempMaxC = tempMaxC;
        }
        Name = name ?? Name;
        Touch(now);
    }

    public void Retire(DateTimeOffset now)
    {
        EnsureEditable();
        Active = false;
        Touch(now);
    }

    private void EnsureEditable()
    {
        if (IsSystem)
            throw new DomainException("The system zone is managed by the platform.");
        if (!Active)
            throw new DomainException("Zone is retired.");
    }

    private static void ValidateRange(decimal? min, decimal? max)
    {
        if (min.HasValue != max.HasValue)
            throw new DomainException("tempMinC and tempMaxC must both be set or both be null.");
        if (min >= max)
            throw new DomainException("tempMinC must be lower than tempMaxC.");
    }
}

public sealed class ZoneAllowedHazardClass
{
    private ZoneAllowedHazardClass() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ZoneId { get; private set; }
    public string HazardClass { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = "";

    public static ZoneAllowedHazardClass Create(Guid tenantId, Guid zoneId, string hazardClass, string actor, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = tenantId, ZoneId = zoneId, HazardClass = hazardClass, CreatedAt = now, CreatedBy = actor,
    };
}
