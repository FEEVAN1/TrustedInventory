using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Application.Saas;

/// <summary>A validated signup ready to become a provisioning job. Identity fields come from the token only.</summary>
public sealed record NewSignup(
    string OwnerSubject,
    string OwnerEmail,
    string SignupKey,
    byte[] RequestDigest,
    string BusinessName,
    string Slug,
    string BillingCountry,
    Guid PlanVersionId,
    string TermsVersion,
    string PrivacyVersion);

public sealed record ProvisioningJobView(
    Guid Id,
    Guid TenantId,
    string RequestedBySubject,
    ProvisioningStatus Status,
    ProvisioningStep? CurrentStep,
    bool Retryable,
    string? ErrorCode,
    DateTimeOffset? ReadyAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// The durable, resumable provisioning job (ONB-002) on the control-plane pool. Each step runs in its own
/// transaction together with its job-state update, so a crash between steps resumes without duplicates.
/// </summary>
public interface IProvisioningEngine
{
    /// <summary>Creates the job, or returns the existing one for the same signup key and body (AT-ONB-01).</summary>
    Task<ProvisioningJobView> CreateOrGetJobAsync(NewSignup signup, CancellationToken ct);

    /// <summary>Runs pending steps under a lease. Failures are recorded on the job, never thrown.</summary>
    Task<ProvisioningJobView> RunAsync(Guid jobId, CancellationToken ct);

    /// <summary>Resumes jobs whose retry time has come and whose lease has lapsed (worker).</summary>
    Task<int> RunDueAsync(CancellationToken ct);

    Task<ProvisioningJobView?> FindAsync(Guid jobId, CancellationToken ct);
}

/// <summary>
/// Test seam for resumption evidence (AT-ONB-01): invoked after each step commits. The production implementation
/// does nothing.
/// </summary>
public interface IProvisioningFaults
{
    void AfterStepCommitted(Guid jobId, ProvisioningStep step);
}

public sealed class NoProvisioningFaults : IProvisioningFaults
{
    public void AfterStepCommitted(Guid jobId, ProvisioningStep step) { }
}

/// <summary>A published, public plan with its currently purchasable prices (GET /saas/plans).</summary>
public sealed record PlanView(
    Guid Id, string PlanCode, int Version, string Name, IReadOnlyList<string> Features,
    IReadOnlyDictionary<string, long?> Limits, int TrialDays, DateTimeOffset PublishedAt, IReadOnlyList<PriceView> Prices, int Revision);

public sealed record PriceView(Guid Id, Guid PlanVersionId, string Currency, long MinorUnits, int Exponent, string Interval,
    DateTimeOffset ActiveFrom, DateTimeOffset? ActiveTo, bool DisplayApproved);

public interface IPlanCatalog
{
    Task<IReadOnlyList<PlanView>> ListPublishedAsync(DateTimeOffset now, CancellationToken ct);
}
