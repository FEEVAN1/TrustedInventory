using TrustedInventory.Domain.Common;

namespace TrustedInventory.Domain.Catalog;

/// <summary>Unit of measure (section 07). Products hold one base unit; conversions map alternates to it.</summary>
public sealed class Unit : TenantEntity
{
    private Unit() { }

    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public bool Active { get; private set; }

    public static Unit Create(Guid tenantId, string code, string name, string actor, DateTimeOffset now)
    {
        var unit = new Unit { Code = code, Name = name, Active = true };
        unit.Stamp(tenantId, actor, now);
        return unit;
    }

    public void Rename(string name, DateTimeOffset now)
    {
        EnsureActive();
        Name = name;
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
            throw new DomainException("Unit is retired.");
    }
}
