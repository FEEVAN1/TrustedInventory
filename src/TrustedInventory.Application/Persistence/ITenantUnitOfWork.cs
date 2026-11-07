using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Security;

namespace TrustedInventory.Application.Persistence;

/// <summary>
/// One database transaction with the caller's tenant context set transaction-locally (DEC-013, TEN-003).
/// Audit events and outbox messages recorded during the work are written in the same transaction just
/// before commit, the audit stream lock last (ARC-003, docs/migration-design.md section 5).
/// </summary>
public interface ITenantSession
{
    DbContext Db { get; }
    CallerContext Caller { get; }
    DateTimeOffset Now { get; }

    void Audit(AuditEntry entry);

    void Publish(string eventType, object payload);
}

/// <summary>Redacted before/after snapshots only: callers pass explicit snapshot objects, never entities.</summary>
public sealed record AuditEntry(
    string EventType,
    string ObjectType,
    Guid? ObjectId,
    int? ObjectVersion,
    string? Reason,
    object? Before,
    object? After);

public interface ITenantUnitOfWork
{
    /// <summary>Read-only transaction with tenant context; audit and outbox calls are rejected.</summary>
    Task<T> ReadAsync<T>(Func<ITenantSession, CancellationToken, Task<T>> work, CancellationToken ct = default);

    /// <summary>Read-write transaction. Deadlocks and serialization failures retry up to three times (STK-005).</summary>
    Task<T> WriteAsync<T>(Func<ITenantSession, CancellationToken, Task<T>> work, CancellationToken ct = default);

    /// <summary>
    /// Read-write transaction guarded by an idempotency record (STK-007). The same key and payload replay the
    /// stored outcome; a different payload fails with IDEMPOTENCY_MISMATCH.
    /// </summary>
    Task<CommandOutcome> WriteIdempotentAsync(IdempotencyRequest request,
        Func<ITenantSession, CancellationToken, Task<CommandOutcome>> work, CancellationToken ct = default);
}

public sealed record IdempotencyRequest(Guid Key, string Route, JsonNode? Payload);

/// <summary>The HTTP status and JSON body a command produced; stored verbatim for replay.</summary>
public sealed record CommandOutcome(int Status, JsonNode? Body, bool Replayed = false);
