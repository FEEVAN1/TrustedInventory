namespace TrustedInventory.Application.Jobs;

/// <summary>A background job whose single pass can be run and tested in isolation (NFR-JOBS).</summary>
public interface IJob
{
    Task RunOnceAsync(CancellationToken ct);
}

/// <summary>A committed outbox event handed to the delivery target after commit (ARC-003).</summary>
public sealed record OutboxMessage(Guid TenantId, Guid EventId, string EventType, string PayloadJson, int Attempt);

/// <summary>
/// Delivery target for outbox events. Consumers deduplicate on <see cref="OutboxMessage.EventId"/>; a throw
/// means "not delivered" and the event is retried with backoff.
/// </summary>
public interface IOutboxSink
{
    Task DeliverAsync(OutboxMessage message, CancellationToken ct);
}

/// <summary>A signed daily audit chain checkpoint (AUD-003).</summary>
public sealed record AuditCheckpointRecord(
    Guid TenantId, DateOnly Date, long Sequence, string EventHashHex, string SignatureHex, string SigningKeyId);

/// <summary>
/// Separately administered, retention-protected storage for checkpoints (AUD-003). Returns a reference to the
/// stored copy. Production storage is an owner decision (D06).
/// </summary>
public interface IRetentionStore
{
    Task<string> WriteCheckpointAsync(AuditCheckpointRecord record, CancellationToken ct);

    /// <summary>Every checkpoint kept for a tenant, read back for independent verification (AT-AUD-01, DEC-093).</summary>
    Task<IReadOnlyList<AuditCheckpointRecord>> ReadCheckpointsAsync(Guid tenantId, CancellationToken ct);
}
