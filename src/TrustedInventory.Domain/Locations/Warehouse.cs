using TrustedInventory.Domain.Common;

namespace TrustedInventory.Domain.Locations;

public sealed class Warehouse : TenantEntity
{
    private Warehouse() { }

    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    /// <summary>IANA zone id; expiry is evaluated at local midnight here (section 03).</summary>
    public string TimeZone { get; private set; } = "";
    public bool Active { get; private set; }

    public static Warehouse Create(Guid tenantId, string code, string name, string timeZone, string actor, DateTimeOffset now)
    {
        var warehouse = new Warehouse { Code = code, Name = name, TimeZone = timeZone, Active = true };
        warehouse.Stamp(tenantId, actor, now);
        return warehouse;
    }

    public void Update(string? name, string? timeZone, DateTimeOffset now)
    {
        EnsureActive();
        Name = name ?? Name;
        TimeZone = timeZone ?? TimeZone;
        Touch(now);
    }

    public void Retire(DateTimeOffset now)
    {
        EnsureActive();
        Active = false;
        Touch(now);
    }

    private void EnsureActive()
    {
        if (!Active)
            throw new DomainException("Warehouse is retired.");
    }
}
