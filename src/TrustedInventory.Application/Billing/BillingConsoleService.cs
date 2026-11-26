using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Organization;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Application.Billing;

public sealed record ReconciliationTaskDto(Guid Id, ReconciliationTaskKind Kind, ReconciliationTaskStatus Status, Guid? TenantId, string Detail,
    DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt, string? ResolvedBy, string? Resolution);

public sealed record ResolveReconciliationTaskRequest(string Resolution);

public sealed record WebhookEventDto(Guid Id, string Provider, string EventId, string EventType, WebhookProcessingState State, DateTimeOffset? ProviderEventAt,
    DateTimeOffset ReceivedAt, DateTimeOffset? ProcessedAt, int Retries, string? FailureCode);

/// <summary>Control-plane reads and the one write of the platform billing consoles (DEC-066). Pages are newest first by (created, id).</summary>
public interface IBillingConsoleStore
{
    Task<IReadOnlyList<ReconciliationTaskDto>> ListTasksAsync(ReconciliationTaskStatus? status, ReconciliationTaskKind? kind, CursorPosition? after, int take,
        CancellationToken ct);

    /// <summary>Resolves an open task; 404 when missing, 409 when already resolved. Audited in the named tenant's chain.</summary>
    Task<ReconciliationTaskDto> ResolveAsync(Guid id, PlatformCaller caller, string resolution, DateTimeOffset now, CancellationToken ct);

    Task<IReadOnlyList<WebhookEventDto>> ListWebhookEventsAsync(WebhookProcessingState? state, CursorPosition? after, int take, CancellationToken ct);
}

/// <summary>
/// Platform billing consoles (BIL-005, BIL-006, BIL-008, SOP-003, DEC-066): operator tasks and inbox metadata for PlatformBilling only.
/// Resolving a task records who, when and how; it never changes stock or access (BIL-008).
/// </summary>
public sealed class BillingConsoleService(IBillingConsoleStore store, ICursorCodec cursors, TimeProvider clock)
{
    public async Task<Page<ReconciliationTaskDto>> ListTasksAsync(PlatformCaller caller, ReconciliationTaskStatus? status, ReconciliationTaskKind? kind,
        PageRequest page, CancellationToken ct)
    {
        caller.RequireBilling();
        page.EnsureSupportedSort();
        var filter = $"billing-tasks|{status}|{kind}";
        var after = page.Cursor is { } c ? cursors.Decode(c, filter) : null;
        var rows = await store.ListTasksAsync(status, kind, after, page.EffectiveLimit + 1, ct);
        return Paged(rows, page.EffectiveLimit, r => new CursorPosition(r.CreatedAt, r.Id), filter);
    }

    public async Task<ReconciliationTaskDto> ResolveAsync(PlatformCaller caller, Guid id, ResolveReconciliationTaskRequest request, CancellationToken ct)
    {
        caller.RequireBilling();
        var resolution = request.Resolution?.Trim();
        if (string.IsNullOrEmpty(resolution) || resolution.Length > 1000)
            throw AppException.Validation("resolution", "Describe the resolution in 1 to 1000 characters.");
        return await store.ResolveAsync(id, caller, resolution, clock.GetUtcNow(), ct);
    }

    public async Task<Page<WebhookEventDto>> ListWebhookEventsAsync(PlatformCaller caller, WebhookProcessingState? state, PageRequest page, CancellationToken ct)
    {
        caller.RequireBilling();
        page.EnsureSupportedSort();
        var filter = $"webhook-events|{state}";
        var after = page.Cursor is { } c ? cursors.Decode(c, filter) : null;
        var rows = await store.ListWebhookEventsAsync(state, after, page.EffectiveLimit + 1, ct);
        return Paged(rows, page.EffectiveLimit, r => new CursorPosition(r.ReceivedAt, r.Id), filter);
    }

    private Page<T> Paged<T>(IReadOnlyList<T> rows, int limit, Func<T, CursorPosition> position, string filter)
    {
        var items = rows.Take(limit).ToList();
        return new Page<T>(items, rows.Count > limit ? cursors.Encode(position(items[^1]), filter) : null);
    }
}
