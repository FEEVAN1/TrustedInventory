namespace TrustedInventory.Domain.Alerts;

// Values match the PostgreSQL enum labels (docs/enums.md); never reorder or rename.

public enum AlertType
{
    Excursion,
    StaleDevice,
    ExpiryApproaching,
    ReconciliationMismatch,
    AuditChainFailure,
    OutboxDeadLetter,
    PutawayException,
}

public enum AlertSeverity
{
    Info,
    Warning,
    High,
}

public enum AlertStatus
{
    Open,
    Acknowledged,
    Closed,
}
