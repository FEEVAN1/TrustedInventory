using TrustedInventory.Application.Common;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Application.Organization;

/// <summary>
/// Tenant service metadata as stored (tenant row, subscription summary, provisioning job). Never includes stock,
/// catalog or billing documents (SAS-004, SOP-006).
/// </summary>
public sealed record PlatformTenantRow(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, string Name, string Slug,
    TenantLifecycle Lifecycle, string HomeRegion, DateTimeOffset? ReadyAt, DateTimeOffset? ArchivedAt,
    DateTimeOffset? DeletionRequestedAt, bool RetentionHold, DateTimeOffset? SecuritySuspendedAt, string? SecuritySuspensionReason,
    string? SecuritySuspendedBy, SubscriptionState? SubscriptionState, DateTimeOffset? TrialEnd, DateTimeOffset? PeriodEnd,
    DateTimeOffset? GraceEnd, bool CancelAtPeriodEnd, string? PlanCode, Guid? ProvisioningJobId, string? ProvisioningStatus)
{
    /// <summary>Access state for the mode computation; entitlements are irrelevant to the mode (DEC-033).</summary>
    public TenantAccessState AccessState => new(Id, Lifecycle, SecuritySuspendedAt, SubscriptionState, TrialEnd, PeriodEnd, GraceEnd,
        CancelAtPeriodEnd, null, null, new HashSet<string>(), new Dictionary<QuotaKey, long>(), null);
}

public sealed record ProvisioningJobDto(
    Guid Id, Guid TenantId, string Status, string? CurrentStep, bool Retryable, string? ErrorCode, DateTimeOffset? ReadyAt,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Platform-console reads on the control-plane pool (ti_control); cursor positions are (createdAt, id).</summary>
public interface IPlatformDirectory
{
    Task<IReadOnlyList<PlatformTenantRow>> ListTenantsAsync(TenantLifecycle? lifecycle, string? search, CursorPosition? after, int take,
        CancellationToken ct);

    Task<PlatformTenantRow?> GetTenantAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<ProvisioningJobDto>> ListProvisioningJobsAsync(string? status, CursorPosition? after, int take, CancellationToken ct);

    /// <summary>Resets a Failed job to Pending for the provisioning worker; null when the job does not exist.</summary>
    Task<ProvisioningJobDto?> RetryProvisioningJobAsync(Guid id, CancellationToken ct);
}

/// <summary>A member's view of one workspace for the tenant selector (section 29): access state and plan code.</summary>
public interface IWorkspaceStatusReader
{
    Task<(TenantAccessState? State, string? PlanCode)> ReadAsync(Guid tenantId, CancellationToken ct);
}
