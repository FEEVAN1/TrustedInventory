namespace TrustedInventory.Domain.Common;

/// <summary>
/// Mutable tenant-owned row (section 07): tenant_id, created/updated stamps and an integer version
/// for optimistic concurrency, exposed to clients as the quoted ETag (API-002).
/// </summary>
public abstract class TenantEntity
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();
    public Guid TenantId { get; protected set; }
    public DateTimeOffset CreatedAt { get; protected set; }
    public string CreatedBy { get; protected set; } = "";
    public DateTimeOffset UpdatedAt { get; protected set; }
    public int Version { get; protected set; } = 1;

    protected void Stamp(Guid tenantId, string actor, DateTimeOffset now)
    {
        TenantId = tenantId;
        CreatedBy = actor;
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <summary>Records a change; the persistence layer only writes it if the stored version still matches.</summary>
    protected void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }
}
