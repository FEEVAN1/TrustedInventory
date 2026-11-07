using System.Text.Json.Nodes;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Alerts;

namespace TrustedInventory.Application.Audit;

/// <summary>
/// Read model of one ti.audit_event row. Rows are written only by the audit appender and never updated
/// (AUD-003); EF maps this type for reading.
/// </summary>
public sealed class StoredAuditEvent
{
    private StoredAuditEvent() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public long Sequence { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public string ActorSubject { get; private set; } = "";
    public ActorType ActorType { get; private set; }
    public string? RequestId { get; private set; }
    public string? CorrelationId { get; private set; }
    public string EventType { get; private set; } = "";
    public string ObjectType { get; private set; } = "";
    public Guid? ObjectId { get; private set; }
    public int? ObjectVersion { get; private set; }
    public string? Reason { get; private set; }
    public string? BeforeState { get; private set; }
    public string? AfterState { get; private set; }
    public byte[] PreviousHash { get; private set; } = [];
    public byte[] EventHash { get; private set; } = [];

    /// <summary>The hashed content exactly as the appender built it, for re-verification.</summary>
    public AuditRecord ToRecord() =>
        new(TenantId, Sequence, OccurredAt, ActorSubject, ActorType.ToString(), RequestId, CorrelationId, EventType,
            ObjectType, ObjectId, ObjectVersion, Reason, Parse(BeforeState), Parse(AfterState));

    public static JsonNode? Parse(string? json) => json is null ? null : JsonNode.Parse(json);
}

/// <summary>A signed daily checkpoint as stored in ti.audit_checkpoint (AUD-003).</summary>
public sealed record StoredCheckpoint(DateOnly Date, long Sequence, byte[] EventHash, string SignatureReference);

/// <summary>Audit-chain plumbing that is not plain entity access.</summary>
public interface IAuditSupport
{
    /// <summary>Highest sequence assigned in the caller's tenant stream (0 when empty).</summary>
    Task<long> StreamHeadAsync(ITenantSession session, CancellationToken ct);

    /// <summary>Newest checkpoint at or before <paramref name="maxSequence"/>, if any.</summary>
    Task<StoredCheckpoint?> LatestCheckpointAsync(ITenantSession session, long maxSequence, CancellationToken ct);

    /// <summary>
    /// Opens an operations alert in the session's transaction unless an open alert with the same dedupe key
    /// already exists (OPS-001). Returns true when a new alert was created.
    /// </summary>
    Task<bool> RaiseAlertAsync(ITenantSession session, AlertType type, AlertSeverity severity, string dedupeKey,
        JsonObject details, CancellationToken ct);
}
