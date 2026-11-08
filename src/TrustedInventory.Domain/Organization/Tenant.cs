using TrustedInventory.Domain.Common;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Domain.Organization;

/// <summary>
/// v2.0 tenant status. Kept only for deprecated response fields until migration 0005 drops the column; the
/// database trigger derives it from the security suspension (DEC-042).
/// </summary>
public enum TenantStatus
{
    Active,
    Suspended,
}

/// <summary>
/// Isolated business account (SAS-001). Stores the lifecycle and an optional security suspension; billing
/// restriction is computed from the subscription, never stored here (DEC-033).
/// </summary>
public sealed class Tenant
{
    private Tenant() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public string Slug { get; private set; } = "";
    public TenantLifecycle Lifecycle { get; private set; }
    public string HomeRegion { get; private set; } = "";
    public DateTimeOffset? ReadyAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public DateTimeOffset? DeletionRequestedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public bool RetentionHold { get; private set; }
    public DateTimeOffset? SecuritySuspendedAt { get; private set; }
    public string? SecuritySuspensionReason { get; private set; }
    public string? SecuritySuspendedBy { get; private set; }
    public Guid? ProvisioningJobId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset UpdatedAt { get; private set; }
    public int Version { get; private set; }

    public bool IsSecuritySuspended => SecuritySuspendedAt is not null;

    /// <summary>A new workspace awaiting its provisioning job (ONB-002). It becomes usable only at <see cref="MarkReady"/>.</summary>
    public static Tenant Provision(Guid id, string name, string slug, string homeRegion, Guid? provisioningJobId, string actor,
        DateTimeOffset now) => new()
    {
        Id = id,
        Name = name,
        Slug = slug,
        HomeRegion = homeRegion,
        Lifecycle = TenantLifecycle.Provisioning,
        ProvisioningJobId = provisioningJobId,
        CreatedAt = now,
        CreatedBy = actor,
        UpdatedAt = now,
        Version = 1,
    };

    public void MarkReady(DateTimeOffset now)
    {
        if (Lifecycle == TenantLifecycle.Ready)
            return; // provisioning retries are idempotent (AT-ONB-01)
        if (Lifecycle != TenantLifecycle.Provisioning)
            throw new DomainException($"A {Lifecycle} tenant cannot become ready.");
        Lifecycle = TenantLifecycle.Ready;
        ReadyAt = now;
        Touch(now);
    }

    /// <summary>Platform security suspension (section 26). Only platform operations lift it; payment never does (BIL-007).</summary>
    public void SuspendForSecurity(string reason, string actor, DateTimeOffset now)
    {
        if (Lifecycle == TenantLifecycle.Deleted)
            throw new DomainException("A deleted tenant cannot be suspended.");
        if (IsSecuritySuspended)
            throw new DomainException("The tenant is already suspended for security.");
        SecuritySuspendedAt = now;
        SecuritySuspensionReason = reason;
        SecuritySuspendedBy = actor;
        Touch(now);
    }

    public void LiftSecuritySuspension(DateTimeOffset now)
    {
        if (!IsSecuritySuspended)
            throw new DomainException("The tenant is not suspended for security.");
        SecuritySuspendedAt = null;
        SecuritySuspensionReason = null;
        SecuritySuspendedBy = null;
        Touch(now);
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }
}
