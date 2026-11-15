using TrustedInventory.Domain.Common;

namespace TrustedInventory.Domain.Saas;

/// <summary>cp.closure_status labels (docs/enums.md); never reorder or rename.</summary>
public enum ClosureStatus
{
    Requested,
    ExportWindow,
    Blocked,
    DeletionPending,
    Deleted,
    Withdrawn,
}

/// <summary>A safety or retention obligation that stops closure or deletion (LFC-002, LFC-004).</summary>
public sealed record ClosureBlocker(string Code, long Count, string Detail);

/// <summary>Closure rules (LFC-002, DEC-070), free of persistence so they can be unit tested.</summary>
public static class ClosureRules
{
    public const int DefaultExportWindowDays = 90; // D10 proposed default, not yet owner-approved

    public static bool IsOpen(ClosureStatus status) =>
        status is ClosureStatus.Requested or ClosureStatus.ExportWindow or ClosureStatus.Blocked or ClosureStatus.DeletionPending;

    /// <summary>The typed confirmation the owner must send: exactly "CLOSE &lt;slug&gt;" (case-sensitive, no padding).</summary>
    public static string Confirmation(string slug) => $"CLOSE {slug}";

    public static bool ConfirmationMatches(string slug, string? confirmation) =>
        !string.IsNullOrEmpty(slug) && string.Equals(confirmation, Confirmation(slug), StringComparison.Ordinal);

    /// <summary>
    /// The export window ends a fixed number of days after the subscription ends: the later of now and the paid or trial
    /// period already granted, so closing early never shortens what was paid for.
    /// </summary>
    public static DateTimeOffset ExportWindowEnds(DateTimeOffset now, DateTimeOffset? serviceEnds, int windowDays)
    {
        if (windowDays is < 1 or > 3650)
            throw new DomainException("The export window must be between 1 and 3650 days.");
        var end = serviceEnds is { } s && s > now ? s : now;
        return end.AddDays(windowDays);
    }

    /// <summary>Only a Ready workspace can be closed; Provisioning has nothing to close and a closing one is already closing.</summary>
    public static void EnsureClosable(TenantLifecycle lifecycle)
    {
        if (lifecycle != TenantLifecycle.Ready)
            throw new DomainException(lifecycle switch
            {
                TenantLifecycle.Provisioning => "The workspace is still being set up.",
                TenantLifecycle.Deleted => "The workspace is deleted.",
                _ => "The workspace is already closing.",
            });
    }
}
