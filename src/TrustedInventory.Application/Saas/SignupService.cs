using System.Text.Json;
using System.Text.RegularExpressions;
using FluentValidation;
using TrustedInventory.Application.Audit;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Application.Saas;

public sealed record SignupRequest(
    string BusinessName, string RequestedSlug, string BillingCountry, Guid PlanVersionId, string TermsVersion, string PrivacyVersion);

public sealed record SignupAccepted(Guid TenantId, Guid ProvisioningJobId, ProvisioningStatus Status);

/// <summary>The caller's identity as validated from the token (never from the body, ONB-001).</summary>
public sealed record SignupIdentity(string Subject, string? Email, bool EmailVerified);

public sealed record ProvisioningJobDto(
    Guid Id, Guid TenantId, ProvisioningStatus Status, ProvisioningStep? CurrentStep, bool Retryable, string? ErrorCode,
    DateTimeOffset? ReadyAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static ProvisioningJobDto From(ProvisioningJobView v) =>
        new(v.Id, v.TenantId, v.Status, v.CurrentStep, v.Retryable, v.ErrorCode, v.ReadyAt, v.CreatedAt, v.UpdatedAt);
}

public sealed partial class SignupRequestValidator : AbstractValidator<SignupRequest>
{
    /// <summary>Slugs that would shadow product routes (SAS-003); never assignable to a tenant.</summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
        "admin", "api", "app", "billing", "platform", "plans", "signup", "login", "status", "support", "www",
    };

    public SignupRequestValidator()
    {
        RuleFor(x => x.BusinessName).ValidName();
        RuleFor(x => x.RequestedSlug).NotEmpty()
            .Must(s => SlugPattern().IsMatch(SignupService.NormalizeSlug(s)))
            .WithMessage("Use 3 to 63 lowercase letters, digits or hyphens, starting and ending with a letter or digit.")
            .Must(s => !Reserved.Contains(SignupService.NormalizeSlug(s))).WithMessage("This address is reserved.");
        RuleFor(x => x.BillingCountry).NotEmpty().Matches("^[A-Z]{2}$").WithMessage("Use an ISO 3166-1 alpha-2 code, e.g. NG.");
        RuleFor(x => x.PlanVersionId).NotEmpty();
        RuleFor(x => x.TermsVersion).NotEmpty().MaximumLength(50);
        RuleFor(x => x.PrivacyVersion).NotEmpty().MaximumLength(50);
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])$")]
    private static partial Regex SlugPattern();
}

/// <summary>
/// Self-service signup (ONB-001, ONB-002, ONB-007). The identity must be email-verified; the owner subject and
/// email come from the token. The provisioning job is created idempotently and run at once; a worker resumes
/// it if this request stops part-way.
/// </summary>
public sealed class SignupService(IProvisioningEngine engine, IValidator<SignupRequest> validator)
{
    public static string NormalizeSlug(string? slug) => (slug ?? "").Trim().ToLowerInvariant();

    public async Task<SignupAccepted> SignupAsync(SignupIdentity identity, Guid idempotencyKey, SignupRequest request, CancellationToken ct)
    {
        if (!identity.EmailVerified || string.IsNullOrWhiteSpace(identity.Email))
            throw AppException.Forbidden("Verify your email address before creating a workspace.");
        validator.ValidateOrThrow(request);

        var normalized = request with
        {
            RequestedSlug = NormalizeSlug(request.RequestedSlug),
            BusinessName = request.BusinessName.Trim(),
        };
        var digest = CanonicalJson.PayloadDigest(JsonSerializer.SerializeToNode(normalized, JsonDefaults.Options));
        var signup = new NewSignup(identity.Subject, identity.Email.Trim().ToLowerInvariant(),
            $"{identity.Subject}:{idempotencyKey:D}", digest, normalized.BusinessName, normalized.RequestedSlug,
            normalized.BillingCountry, normalized.PlanVersionId, normalized.TermsVersion, normalized.PrivacyVersion);

        var job = await engine.CreateOrGetJobAsync(signup, ct);
        if (job.Status is ProvisioningStatus.Pending or ProvisioningStatus.Provisioning)
            job = await engine.RunAsync(job.Id, ct);
        return new SignupAccepted(job.TenantId, job.Id, job.Status);
    }

    /// <summary>Only the identity that signed up sees the job; anyone else gets the same 404 as an unknown id.</summary>
    public async Task<ProvisioningJobDto> GetJobAsync(string subject, Guid jobId, CancellationToken ct)
    {
        var job = await engine.FindAsync(jobId, ct);
        if (job is null || job.RequestedBySubject != subject)
            throw AppException.NotFound("Provisioning job");
        return ProvisioningJobDto.From(job);
    }
}

public sealed record PriceDto(Guid Id, Guid PlanVersionId, MoneyDto Amount, string Interval, DateTimeOffset ActiveFrom,
    DateTimeOffset? ActiveTo, bool DisplayApproved);

public sealed record MoneyDto(string Currency, long MinorUnits, int Exponent);

public sealed record PlanDto(
    Guid Id, string PlanCode, int Version, string Name, string Status, IReadOnlyList<string> Features,
    IReadOnlyDictionary<string, long?> Limits, int TrialDays, DateTimeOffset? PublishedAt, IReadOnlyList<PriceDto> Prices);

/// <summary>Public plan catalog (ENT-001, section 28): published, public versions with current prices only.</summary>
public sealed class PlanCatalogService(IPlanCatalog catalog, ICursorCodec cursors, TimeProvider clock, SaasOptions options)
{
    public async Task<Page<PlanDto>> ListAsync(PageRequest page, CancellationToken ct)
    {
        page.EnsureSupportedSort();
        var limit = page.EffectiveLimit;
        var plans = (await catalog.ListPublishedAsync(clock.GetUtcNow(), ct))
            .OrderByDescending(p => p.PublishedAt).ThenByDescending(p => p.Id)
            .ToList();
        if (page.Cursor is { } cursor)
        {
            var p = cursors.Decode(cursor, "plans");
            plans = plans.Where(x => x.PublishedAt < p.CreatedAt || (x.PublishedAt == p.CreatedAt && x.Id.CompareTo(p.Id) < 0)).ToList();
        }

        var items = plans.Take(limit).ToList();
        var next = plans.Count > limit ? cursors.Encode(new CursorPosition(items[^1].PublishedAt, items[^1].Id), "plans") : null;
        return new Page<PlanDto>(items.Select(p => new PlanDto(p.Id, p.PlanCode, p.Version, p.Name, "Published", p.Features,
            p.Limits, options.TrialDays, p.PublishedAt,
            // No display price is approved yet (D09, DEC-040): fixtures are listed but flagged as not for display.
            p.Prices.Select(x => new PriceDto(x.Id, x.PlanVersionId, new MoneyDto(x.Currency, x.MinorUnits, x.Exponent),
                x.Interval, x.ActiveFrom, x.ActiveTo, DisplayApproved: false)).ToList())).ToList(), next);
    }
}

/// <summary>SaaS defaults in force until the owner approves commercial terms (D09, D10, DEC-040, DEC-044).</summary>
public sealed record SaasOptions(int TrialDays, string HomeRegion)
{
    /// <summary>New workspaces one identity may create per rolling 24 hours (ONB-007).</summary>
    public int SignupsPerIdentityPerDay { get; init; } = 3;

    /// <summary>New invitations one tenant may create per rolling hour (ONB-007).</summary>
    public int InvitationsPerTenantPerHour { get; init; } = 30;

    public static readonly TimeSpan SignupWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan InvitationWindow = TimeSpan.FromHours(1);
}
