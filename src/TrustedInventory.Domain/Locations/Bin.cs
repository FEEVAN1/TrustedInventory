using TrustedInventory.Domain.Common;

namespace TrustedInventory.Domain.Locations;

public enum BinKind
{
    Storage,
    Staging,
    Quarantine,
    /// <summary>System-managed, one per warehouse (DEC-010). Never created or edited through the API.</summary>
    Transit,
}

public sealed class Bin : TenantEntity
{
    public const string TransitBinCode = "TRANSIT";

    private Bin() { }

    public Guid WarehouseId { get; private set; }
    public Guid ZoneId { get; private set; }
    public string Code { get; private set; } = "";
    public BinKind Kind { get; private set; }
    public int PickPriority { get; private set; }
    public decimal? Capacity { get; private set; }
    public Guid? CapacityUomId { get; private set; }
    public bool Active { get; private set; }

    public static Bin Create(Guid tenantId, Guid warehouseId, Guid zoneId, string code, BinKind kind, int pickPriority,
        decimal? capacity, Guid? capacityUomId, string actor, DateTimeOffset now)
    {
        if (kind == BinKind.Transit)
            throw new DomainException("Transit bins are created by the system.");
        ValidateCapacity(capacity, capacityUomId);
        var bin = new Bin
        {
            WarehouseId = warehouseId, ZoneId = zoneId, Code = code, Kind = kind, PickPriority = pickPriority,
            Capacity = capacity, CapacityUomId = capacityUomId, Active = true,
        };
        bin.Stamp(tenantId, actor, now);
        return bin;
    }

    public static Bin CreateTransit(Guid tenantId, Guid warehouseId, Guid systemZoneId, string actor, DateTimeOffset now)
    {
        var bin = new Bin
        {
            WarehouseId = warehouseId, ZoneId = systemZoneId, Code = TransitBinCode, Kind = BinKind.Transit,
            PickPriority = 0, Active = true,
        };
        bin.Stamp(tenantId, actor, now);
        return bin;
    }

    public void Update(int? pickPriority, decimal? capacity, Guid? capacityUomId, bool capacitySpecified, DateTimeOffset now)
    {
        EnsureEditable();
        if (capacitySpecified)
        {
            ValidateCapacity(capacity, capacityUomId);
            Capacity = capacity;
            CapacityUomId = capacityUomId;
        }
        PickPriority = pickPriority ?? PickPriority;
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
        if (Kind == BinKind.Transit)
            throw new DomainException("Transit bins are managed by the system.");
        if (!Active)
            throw new DomainException("Bin is retired.");
    }

    private static void ValidateCapacity(decimal? capacity, Guid? uomId)
    {
        if (capacity.HasValue != uomId.HasValue)
            throw new DomainException("capacity and capacityUomId must both be set or both be null.");
    }
}
