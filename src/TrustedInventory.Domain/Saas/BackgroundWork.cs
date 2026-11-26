namespace TrustedInventory.Domain.Saas;

/// <summary>Background jobs implemented so far; rows of the "Background work by mode" table.</summary>
public enum BackgroundJob
{
    OutboxDelivery,
    IdempotencyCleanup,
    /// <summary>Row "Audit checkpoints": also for Deleted tenants, whose retained chain is deletion evidence (DEC-072).</summary>
    AuditCheckpoint,
    /// <summary>Row "Ledger reconciliation and audit verification": recomputes bucket projections from the ledger (STK-004, DEC-049).</summary>
    LedgerReconciliation,
    /// <summary>Row "Reservation expiry (releases stock, never consumes it)" (STK-008, DEC-055).</summary>
    ReservationExpiry,
    /// <summary>Row "Export generation (already requested exports)" (AUD-004, DEC-061).</summary>
    ExportGeneration,
    /// <summary>Row "Billing reminders and reconciliation": relays billing notices to the tenant outbox (DEC-064).</summary>
    BillingNotices,
    /// <summary>Row "Telemetry evaluation, stale detection, excursion holds": stale device alerts (TEL-005, DEC-077).</summary>
    TelemetryEvaluation,
}

/// <summary>
/// The "Background work by mode" table in docs/saas-state-matrix.md, as code (DEC-043). Keep the two identical:
/// a unit test walks every cell. Background work is never entitlement-gated.
/// </summary>
public static class BackgroundWork
{
    public static bool Runs(BackgroundJob job, AccessMode mode) => job switch
    {
        // Deleted: only audit checkpoints, which sign the retained chain (deletion evidence, DEC-072). No other work.
        BackgroundJob.AuditCheckpoint => true,
        BackgroundJob.OutboxDelivery or BackgroundJob.IdempotencyCleanup
            or BackgroundJob.LedgerReconciliation or BackgroundJob.ReservationExpiry or BackgroundJob.ExportGeneration
            or BackgroundJob.BillingNotices or BackgroundJob.TelemetryEvaluation => mode != AccessMode.Deleted,
        _ => false,
    };

    /// <summary>
    /// Decides from lifecycle alone, which is all the worker's ti_platform pool can read (no control-plane access,
    /// SAS-004). Valid because every row is identical across Normal, BillingRestricted and SecuritySuspended; a unit
    /// test enforces that, so a job that breaks it must read billing state instead.
    /// </summary>
    public static bool RunsFor(BackgroundJob job, TenantLifecycle lifecycle) => lifecycle switch
    {
        TenantLifecycle.Provisioning => Runs(job, AccessMode.Provisioning),
        TenantLifecycle.Ready => Runs(job, AccessMode.Normal),
        TenantLifecycle.Archived or TenantLifecycle.DeletionPending => Runs(job, AccessMode.Closed),
        TenantLifecycle.Deleted => Runs(job, AccessMode.Deleted),
        _ => false, // any unknown value fails closed: no work at all
    };
}
