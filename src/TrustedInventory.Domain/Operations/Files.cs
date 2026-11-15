using TrustedInventory.Domain.Common;

namespace TrustedInventory.Domain.Operations;

public enum AttachmentScanStatus
{
    Pending, Clean, Rejected,
}

public enum ExportType
{
    Audit, Movements, Traceability, StockSnapshot, TenantArchive,
}

public enum ExportStatus
{
    Queued, Running, Completed, Failed, Expired,
}

/// <summary>An evidence file under the tenant's object prefix (TEN-005, DEC-022, DEC-061).</summary>
public sealed class Attachment : TenantEntity
{
    public const long MaxBytes = 10 * 1024 * 1024;

    private Attachment() { }

    public string? EntityType { get; private set; }
    public Guid? EntityId { get; private set; }
    public string FileName { get; private set; } = "";
    public string ObjectKey { get; private set; } = "";
    public string MediaType { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public byte[] DigestSha256 { get; private set; } = [];
    public AttachmentScanStatus ScanStatus { get; private set; }

    public static Attachment Create(Guid id, Guid tenantId, string fileName, string mediaType, long size, byte[] digest, bool clean, string? entityType,
        Guid? entityId, string actor, DateTimeOffset now)
    {
        if (size is <= 0 or > MaxBytes)
            throw new DomainException("Attachments are 1 byte to 10 MiB.");
        if ((entityType is null) != (entityId is null))
            throw new DomainException("Bind an attachment to an entity type and id together.");
        var attachment = new Attachment
        {
            Id = id, EntityType = entityType, EntityId = entityId, FileName = fileName, ObjectKey = KeyFor(tenantId, id), MediaType = mediaType,
            SizeBytes = size, DigestSha256 = digest, ScanStatus = clean ? AttachmentScanStatus.Clean : AttachmentScanStatus.Rejected,
        };
        attachment.Stamp(tenantId, actor, now);
        return attachment;
    }

    public static string KeyFor(Guid tenantId, Guid id) => $"{tenantId:D}/attachments/{id:D}";
}

/// <summary>An asynchronous export (AUD-004, NFR-EXPORTS, DEC-061): Queued → Running → Completed or Failed.</summary>
public sealed class ExportJob : TenantEntity
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private ExportJob() { }

    public ExportType Type { get; private set; }
    public ExportStatus Status { get; private set; }
    public string Filter { get; private set; } = "{}";
    public byte[] FilterDigest { get; private set; } = [];
    public long? StreamFromSeq { get; private set; }
    public long? StreamToSeq { get; private set; }
    public string? ObjectKey { get; private set; }
    public string? Manifest { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? RetainUntil { get; private set; }

    public static ExportJob Queue(Guid tenantId, ExportType type, string filterJson, byte[] filterDigest, string actor, DateTimeOffset now)
    {
        var job = new ExportJob { Type = type, Status = ExportStatus.Queued, Filter = filterJson, FilterDigest = filterDigest };
        job.Stamp(tenantId, actor, now);
        return job;
    }

    public void Start(DateTimeOffset now)
    {
        if (Status != ExportStatus.Queued)
            throw new DomainException("Only a queued export can start.");
        Status = ExportStatus.Running;
        Touch(now);
    }

    public void Complete(long? from, long? to, string manifestJson, DateTimeOffset now)
    {
        if (Status != ExportStatus.Running)
            throw new DomainException("Only a running export can complete.");
        Status = ExportStatus.Completed;
        StreamFromSeq = from;
        StreamToSeq = to;
        ObjectKey = $"{TenantId:D}/exports/{Id:D}.{(Type == ExportType.TenantArchive ? "zip" : "jsonl")}";
        Manifest = manifestJson;
        CompletedAt = now;
        RetainUntil = now + Retention;
        Touch(now);
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        if (Status is not (ExportStatus.Queued or ExportStatus.Running))
            throw new DomainException("Only an unfinished export can fail.");
        Status = ExportStatus.Failed;
        FailureReason = reason.Length > 2000 ? reason[..2000] : reason;
        Touch(now);
    }
}
