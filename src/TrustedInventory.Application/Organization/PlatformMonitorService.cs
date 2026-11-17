using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;

namespace TrustedInventory.Application.Organization;

public enum PlatformAlertKind
{
    WebhookBacklog,
    EntitlementMismatch,
    ProvisioningFailures,
    ReconciliationBacklog,
    UnpaidInvoices,
}

public enum PlatformAlertStatus
{
    Open,
    Acknowledged,
    Closed,
}

public sealed record PlatformAlertDto(Guid Id, PlatformAlertKind Kind, string Severity, PlatformAlertStatus Status, IReadOnlyDictionary<string, long> Detail,
    DateTimeOffset OpenedAt, DateTimeOffset LastSeenAt, string? AcknowledgedBy, DateTimeOffset? AcknowledgedAt, string? Acknowledgement,
    DateTimeOffset? ClosedAt, int Version) : IVersioned;

public sealed record AcknowledgeAlertRequest(string Note);

/// <summary>Platform-wide service signals (SOP-003): counts, ages and latencies only, never a tenant's name, data or stock.</summary>
public sealed record PlatformHealthDto(
    DateTimeOffset MeasuredAt,
    long SignupsLast24h, long SignupsReadyLast24h, long SignupsFailedLast24h,
    double? ProvisioningP50Seconds, double? ProvisioningP95Seconds,
    long TrialsEndingWithin3Days, long TrialsEndedRestricted,
    long WebhookBacklog, double? OldestWebhookAgeSeconds,
    long EntitlementMismatchTasks, long OpenReconciliationTasks, long StaleReconciliationTasks, long UnpaidInvoices,
    long SeatCounterDivergences, long OpenAlerts);

/// <summary>One signal measured by the monitor: the alert it raises when present, with a dedupe key per kind.</summary>
public sealed record AlertSignal(PlatformAlertKind Kind, string Severity, bool Present, IReadOnlyDictionary<string, long> Detail);

public interface IPlatformMonitorStore
{
    Task<PlatformHealthDto> MeasureAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>Opens (or refreshes) an alert for each present signal and closes alerts whose condition cleared.</summary>
    Task<int> ApplySignalsAsync(IReadOnlyList<AlertSignal> signals, DateTimeOffset now, CancellationToken ct);

    Task<IReadOnlyList<PlatformAlertDto>> ListAsync(PlatformAlertStatus? status, CursorPosition? after, int take, CancellationToken ct);

    Task<PlatformAlertDto?> AcknowledgeAsync(Guid id, int expectedVersion, string subject, string note, DateTimeOffset now, CancellationToken ct);
}

/// <summary>
/// Platform operations monitoring (SOP-003, DEC-074). Thresholds: webhook backlog older than five minutes and any entitlement
/// mismatch are Critical; provisioning failures, reconciliation tasks open for more than a day and unpaid invoices are Warning.
/// A billing provider outage raises the backlog alert but never restricts tenants (DEC-066).
/// </summary>
public sealed class PlatformMonitorService(IPlatformMonitorStore store, ICursorCodec cursors, TimeProvider clock)
{
    public static readonly TimeSpan WebhookAgeThreshold = TimeSpan.FromMinutes(5);

    public Task<PlatformHealthDto> GetHealthAsync(PlatformCaller caller, CancellationToken ct)
    {
        RequireHealthReader(caller);
        return store.MeasureAsync(clock.GetUtcNow(), ct);
    }

    public async Task<Page<PlatformAlertDto>> ListAsync(PlatformCaller caller, PlatformAlertStatus? status, PageRequest page, CancellationToken ct)
    {
        RequireHealthReader(caller);
        page.EnsureSupportedSort();
        var limit = page.EffectiveLimit;
        var filterKey = $"platform-alerts|{status}";
        var after = page.Cursor is { } c ? cursors.Decode(c, filterKey) : null;
        var rows = (await store.ListAsync(status, after, limit + 1, ct)).ToList();
        string? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            next = cursors.Encode(new CursorPosition(rows[^1].OpenedAt, rows[^1].Id), filterKey);
        }
        return new Page<PlatformAlertDto>(rows, next);
    }

    public async Task<PlatformAlertDto> AcknowledgeAsync(PlatformCaller caller, Guid id, int expectedVersion, AcknowledgeAlertRequest request,
        CancellationToken ct)
    {
        caller.RequireAdministrator();
        var note = request.Note?.Trim();
        if (string.IsNullOrEmpty(note) || note.Length > 1000)
            throw AppException.Validation("note", "Give a note of 1 to 1000 characters.");
        return await store.AcknowledgeAsync(id, expectedVersion, caller.Subject, note, clock.GetUtcNow(), ct) ?? throw AppException.NotFound("Alert");
    }

    /// <summary>platform.health.read: platform operations and platform billing (docs/permissions.md).</summary>
    public static void RequireHealthReader(PlatformCaller caller)
    {
        if (!caller.IsAdministrator && !caller.IsBilling)
            throw AppException.Forbidden("Platform health is restricted to platform operators.");
    }

    /// <summary>The alert rules applied to one measurement; pure, so the thresholds are unit tested.</summary>
    public static IReadOnlyList<AlertSignal> Evaluate(PlatformHealthDto h) =>
    [
        new(PlatformAlertKind.WebhookBacklog, "Critical", h.OldestWebhookAgeSeconds > WebhookAgeThreshold.TotalSeconds,
            new Dictionary<string, long> { ["backlog"] = h.WebhookBacklog, ["oldestAgeSeconds"] = (long)(h.OldestWebhookAgeSeconds ?? 0) }),
        new(PlatformAlertKind.EntitlementMismatch, "Critical", h.EntitlementMismatchTasks > 0,
            new Dictionary<string, long> { ["openTasks"] = h.EntitlementMismatchTasks }),
        new(PlatformAlertKind.ProvisioningFailures, "Warning", h.SignupsFailedLast24h > 0,
            new Dictionary<string, long> { ["failedLast24h"] = h.SignupsFailedLast24h }),
        new(PlatformAlertKind.ReconciliationBacklog, "Warning", h.StaleReconciliationTasks > 0 || h.SeatCounterDivergences > 0,
            new Dictionary<string, long> { ["tasksOpenOverADay"] = h.StaleReconciliationTasks, ["seatCounterDivergences"] = h.SeatCounterDivergences }),
        new(PlatformAlertKind.UnpaidInvoices, "Warning", h.UnpaidInvoices > 0, new Dictionary<string, long> { ["open"] = h.UnpaidInvoices }),
    ];
}
