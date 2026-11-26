using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Organization;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Application.Billing;

public sealed record ComplimentaryAccessDto(Guid Id, int Version, Guid TenantId, string TenantName, Guid PlanVersionId, string PlanCode,
    DateTimeOffset ExpiresAt, string Reason, ComplimentaryAccessStatus Status, string RequestedBy, DateTimeOffset RequestedAt, string? ApprovedBy,
    DateTimeOffset? ApprovedAt, string? ApprovalReason, string? RevokedBy, DateTimeOffset? RevokedAt, string? RevokeReason) : IVersioned;

public sealed record CreateComplimentaryAccessRequest(Guid TenantId, Guid PlanVersionId, DateTimeOffset ExpiresAt, string Reason);

public sealed record ComplimentaryDecisionRequest(string Reason);

/// <summary>Control-plane store for complimentary grants (DEC-094), on the ti_control pool; every write is audited in the tenant's chain.</summary>
public interface IComplimentaryAccessStore
{
    Task<IReadOnlyList<ComplimentaryAccessDto>> ListAsync(Guid? tenantId, ComplimentaryAccessStatus? status, DateTimeOffset now, CursorPosition? after,
        int take, CancellationToken ct);

    /// <summary>404 for an unknown tenant or plan version; 409 for a closed tenant, an unpublished plan or an open grant.</summary>
    Task<ComplimentaryAccessDto> RequestAsync(PlatformCaller caller, CreateComplimentaryAccessRequest request, DateTimeOffset now, CancellationToken ct);

    Task<ComplimentaryAccessDto> ApproveAsync(PlatformCaller caller, Guid id, int expectedVersion, string reason, DateTimeOffset now, CancellationToken ct);

    Task<ComplimentaryAccessDto> RevokeAsync(PlatformCaller caller, Guid id, int expectedVersion, string reason, DateTimeOffset now, CancellationToken ct);
}

/// <summary>
/// Complimentary access (section 29, BIL-008, SOD-8, DEC-094). A platform billing operator requests a published plan's features and limits for
/// one workspace until an expiry; a different platform operator approves it. While it is in force the workspace has no billing restriction;
/// closure, deletion and a security suspension still apply, and the grant never changes stock, audit or the subscription itself.
/// </summary>
public sealed class ComplimentaryAccessService(IComplimentaryAccessStore store, ICursorCodec cursors, TimeProvider clock)
{
    public static readonly TimeSpan MaximumTerm = TimeSpan.FromDays(366);

    public async Task<Page<ComplimentaryAccessDto>> ListAsync(PlatformCaller caller, Guid? tenantId, ComplimentaryAccessStatus? status, PageRequest page,
        CancellationToken ct)
    {
        caller.RequireCatalogReader();
        page.EnsureSupportedSort();
        var filter = $"complimentary|{tenantId}|{status}";
        var after = page.Cursor is { } c ? cursors.Decode(c, filter) : null;
        var rows = await store.ListAsync(tenantId, status, clock.GetUtcNow(), after, page.EffectiveLimit + 1, ct);
        var items = rows.Take(page.EffectiveLimit).ToList();
        return new Page<ComplimentaryAccessDto>(items,
            rows.Count > page.EffectiveLimit ? cursors.Encode(new CursorPosition(items[^1].RequestedAt, items[^1].Id), filter) : null);
    }

    public Task<ComplimentaryAccessDto> RequestAsync(PlatformCaller caller, CreateComplimentaryAccessRequest request, CancellationToken ct)
    {
        caller.RequireBilling();
        var now = clock.GetUtcNow();
        if (request.TenantId == Guid.Empty)
            throw AppException.Validation("tenantId", "Choose a workspace.");
        if (request.PlanVersionId == Guid.Empty)
            throw AppException.Validation("planVersionId", "Choose a plan version.");
        if (request.ExpiresAt <= now)
            throw AppException.Validation("expiresAt", "The expiry must be in the future.");
        if (request.ExpiresAt > now + MaximumTerm)
            throw AppException.Validation("expiresAt", "Complimentary access lasts at most 366 days; request a new grant to extend it.");
        return store.RequestAsync(caller, request with { Reason = Reason(request.Reason) }, now, ct);
    }

    public Task<ComplimentaryAccessDto> ApproveAsync(PlatformCaller caller, Guid id, int expectedVersion, ComplimentaryDecisionRequest request,
        CancellationToken ct)
    {
        caller.RequireCatalogReader();
        return store.ApproveAsync(caller, id, expectedVersion, Reason(request.Reason), clock.GetUtcNow(), ct);
    }

    public Task<ComplimentaryAccessDto> RevokeAsync(PlatformCaller caller, Guid id, int expectedVersion, ComplimentaryDecisionRequest request,
        CancellationToken ct)
    {
        caller.RequireCatalogReader();
        return store.RevokeAsync(caller, id, expectedVersion, Reason(request.Reason), clock.GetUtcNow(), ct);
    }

    /// <summary>The status at an instant: expiry is computed, never written by a job (section 26).</summary>
    public static ComplimentaryAccessStatus StatusAt(DateTimeOffset? approvedAt, DateTimeOffset? revokedAt, DateTimeOffset expiresAt, DateTimeOffset now) =>
        revokedAt is not null ? ComplimentaryAccessStatus.Revoked
        : now >= expiresAt ? ComplimentaryAccessStatus.Expired
        : approvedAt is not null ? ComplimentaryAccessStatus.Active
        : ComplimentaryAccessStatus.Pending;

    private static string Reason(string? reason)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 1000)
            throw AppException.Validation("reason", "Give a reason of 1 to 1000 characters.");
        return trimmed;
    }
}
