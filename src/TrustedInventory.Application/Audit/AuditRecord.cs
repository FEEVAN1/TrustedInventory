using System.Globalization;
using System.Text.Json.Nodes;

namespace TrustedInventory.Application.Audit;

/// <summary>
/// The hashed content of one audit event (AUD-001, AUD-002). Writer and verifier both build the body
/// through <see cref="CanonicalBody"/>, so a stored row can be re-hashed exactly.
/// </summary>
public sealed record AuditRecord(
    Guid TenantId,
    long Sequence,
    DateTimeOffset OccurredAt,
    string ActorSubject,
    string ActorType,
    string? RequestId,
    string? CorrelationId,
    string EventType,
    string ObjectType,
    Guid? ObjectId,
    int? ObjectVersion,
    string? Reason,
    JsonNode? Before,
    JsonNode? After)
{
    /// <summary>Audit timestamps are stored and hashed at millisecond precision.</summary>
    public static DateTimeOffset Normalize(DateTimeOffset t) =>
        new(t.UtcTicks - t.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    public string CanonicalBody() => CanonicalJson.Serialize(new JsonObject
    {
        ["tenantId"] = TenantId.ToString(),
        ["sequence"] = Sequence,
        ["occurredAt"] = Normalize(OccurredAt).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        ["actorSubject"] = ActorSubject,
        ["actorType"] = ActorType,
        ["requestId"] = RequestId,
        ["correlationId"] = CorrelationId,
        ["eventType"] = EventType,
        ["objectType"] = ObjectType,
        ["objectId"] = ObjectId?.ToString(),
        ["objectVersion"] = ObjectVersion,
        ["reason"] = Reason,
        ["before"] = Before?.DeepClone(),
        ["after"] = After?.DeepClone(),
    });

    public byte[] Hash(byte[] previousHash) => AuditHash.Compute(previousHash, CanonicalBody());
}
