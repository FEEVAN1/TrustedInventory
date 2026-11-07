using TrustedInventory.Domain.Common;

namespace TrustedInventory.Domain.Catalog;

/// <summary>
/// Excursion handling for one storage profile (DEC-027). R1 supports only an immediate hold on the first
/// out-of-range reading (TEL-003); notification recipients stay at the database default until M5.
/// </summary>
public sealed class ExcursionPolicy : TenantEntity
{
    private ExcursionPolicy() { }

    public string Name { get; private set; } = "";
    public bool HoldOnFirstReading { get; private set; }
    public string? Notes { get; private set; }
    public bool Active { get; private set; }

    public static ExcursionPolicy CreateFor(Guid tenantId, Guid storageProfileId, bool immediateHold, string? notes,
        string actor, DateTimeOffset now)
    {
        EnsureImmediateHold(immediateHold);
        var policy = new ExcursionPolicy
        {
            // Policies belong to exactly one profile; the profile id keeps the name unique and stable on rename.
            Name = $"storage-profile:{storageProfileId}", HoldOnFirstReading = true, Notes = notes, Active = true,
        };
        policy.Stamp(tenantId, actor, now);
        return policy;
    }

    public void Update(bool immediateHold, string? notes, DateTimeOffset now)
    {
        EnsureImmediateHold(immediateHold);
        Notes = notes;
        Touch(now);
    }

    public void Retire(DateTimeOffset now)
    {
        Active = false;
        Touch(now);
    }

    private static void EnsureImmediateHold(bool immediateHold)
    {
        if (!immediateHold)
            throw new DomainException("R1 supports only immediate hold on excursion (TEL-003).");
    }
}

/// <summary>Product temperature limits (CFG-002). Limits are product-specific, never universal.</summary>
public sealed class StorageProfile : TenantEntity
{
    private StorageProfile() { }

    public string Name { get; private set; } = "";
    public decimal MinC { get; private set; }
    public decimal MaxC { get; private set; }
    public bool MonitoringRequired { get; private set; }
    public int StaleAfterSeconds { get; private set; }
    public Guid ExcursionPolicyId { get; private set; }
    public bool Active { get; private set; }

    /// <summary>Creates the profile and its own excursion policy (DEC-027).</summary>
    public static (StorageProfile Profile, ExcursionPolicy Policy) Create(Guid tenantId, string name, decimal minC, decimal maxC,
        bool monitoringRequired, int staleAfterSeconds, bool immediateHold, string? notes, string actor, DateTimeOffset now)
    {
        ValidateLimits(minC, maxC);
        var profile = new StorageProfile
        {
            Name = name, MinC = minC, MaxC = maxC, MonitoringRequired = monitoringRequired,
            StaleAfterSeconds = staleAfterSeconds, Active = true,
        };
        profile.Stamp(tenantId, actor, now);
        var policy = ExcursionPolicy.CreateFor(tenantId, profile.Id, immediateHold, notes, actor, now);
        profile.ExcursionPolicyId = policy.Id;
        return (profile, policy);
    }

    public void Update(string name, decimal minC, decimal maxC, bool monitoringRequired, int staleAfterSeconds, DateTimeOffset now)
    {
        EnsureActive();
        ValidateLimits(minC, maxC);
        Name = name;
        MinC = minC;
        MaxC = maxC;
        MonitoringRequired = monitoringRequired;
        StaleAfterSeconds = staleAfterSeconds;
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
            throw new DomainException("Storage profile is retired.");
    }

    private static void ValidateLimits(decimal minC, decimal maxC)
    {
        if (minC >= maxC)
            throw new DomainException("minC must be lower than maxC.");
    }
}
