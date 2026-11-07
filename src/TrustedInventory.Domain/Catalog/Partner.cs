using TrustedInventory.Domain.Common;

namespace TrustedInventory.Domain.Catalog;

/// <summary>Supplier and/or customer (section 07). Operational reference only: no payment or patient data.</summary>
public sealed class Partner : TenantEntity
{
    private Partner() { }

    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public PartnerType Type { get; private set; }
    public bool Active { get; private set; }

    public static Partner Create(Guid tenantId, string code, string name, PartnerType type, string actor, DateTimeOffset now)
    {
        var partner = new Partner { Code = code, Name = name, Type = type, Active = true };
        partner.Stamp(tenantId, actor, now);
        return partner;
    }

    public void Update(string name, PartnerType type, DateTimeOffset now)
    {
        EnsureActive();
        Name = name;
        Type = type;
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
            throw new DomainException("Partner is retired.");
    }
}
