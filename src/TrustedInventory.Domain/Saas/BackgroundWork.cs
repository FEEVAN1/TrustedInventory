namespace TrustedInventory.Domain.Saas;

/// <summary>Background jobs implemented so far; rows of the "Background work by mode" table.</summary>
public enum BackgroundJob
{
    OutboxDelivery,
    IdempotencyCleanup,
    /// <summary>Row "Ledger reconciliation, audit verification and checkpoints".</summary>
    AuditCheckpoint,
    /// <summary>Same row: recomputes bucket projections from the movement ledger (STK-004, DEC-049).</summary>
    LedgerReconciliation,
    /// <summary>Row "Reservation expiry (releases stock, never consumes it)" (STK-008, DEC-055).</summary>
    ReservationExpiry,
    /// <summary>Row "Export generation (already requested exports)" (AUD-004, DEC-061).</summary>
    ExportGeneration,
}

/// <summary>
/// The "Background work by mode" table in docs/saas-state-matrix.md, as code (DEC-043). Keep the two identical:
/// a unit test walks every cell. Background work is never entitlement-gated.
/// </summary>
public static class BackgroundWork
{
    public static bool Runs(BackgroundJob job, AccessMode mode) => job switch
    {
        // Deleted: no work. Checkpoints keep deletion evidence only, which arrives with the deletion flow (S3).
        BackgroundJob.OutboxDelivery or BackgroundJob.IdempotencyCleanup or BackgroundJob.AuditCheckpoint
            or BackgroundJob.LedgerReconciliation or BackgroundJob.ReservationExpiry or BackgroundJob.ExportGeneration => mode != AccessMode.Deleted,
        _ => false,
    };

    /// <summary>
    /// Decides from lifecycle alone, which is all the worker's ti_platform pool can read (no control-plane access,
    /// SAS-004). Valid because every row is identical across Normal, BillingRestricted and SecuritySuspended; a unit
    /// test enforces that, so a job that breaks it must read billing state instead.
    /// </summary>
    public static bool RunsFor(BackgroundJob job, TenantLifecycle lifecycle) => Runs(job, lifecycle switch
    {
        TenantLifecycle.Provisioning => AccessMode.Provisioning,
        TenantLifecycle.Ready => AccessMode.Normal,
        TenantLifecycle.Archived or TenantLifecycle.DeletionPending => AccessMode.Closed,
        _ => AccessMode.Deleted, // Deleted, and any unknown value, fails closed
    });
}
