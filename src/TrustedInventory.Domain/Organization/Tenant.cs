namespace TrustedInventory.Domain.Organization;

public enum TenantStatus
{
    Active,
    Suspended,
}

/// <summary>Isolated business account. Platform-managed; tenant users only read their own row.</summary>
public sealed class Tenant
{
    private Tenant() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public TenantStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset UpdatedAt { get; private set; }
    public int Version { get; private set; }

    public static Tenant Create(string name, string actor, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = name,
        Status = TenantStatus.Active,
        CreatedAt = now,
        CreatedBy = actor,
        UpdatedAt = now,
        Version = 1,
    };

    public void Suspend(DateTimeOffset now) => SetStatus(TenantStatus.Suspended, now);

    public void Reactivate(DateTimeOffset now) => SetStatus(TenantStatus.Active, now);

    private void SetStatus(TenantStatus status, DateTimeOffset now)
    {
        if (Status == status)
            throw new Common.DomainException($"Tenant is already {status}.");
        Status = status;
        UpdatedAt = now;
        Version++;
    }
}
