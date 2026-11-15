namespace TrustedInventory.Application.Operations;

/// <summary>Tenant-prefixed object storage (TEN-005, DEC-061). The production store is an owner decision (D06).</summary>
public interface IObjectStore
{
    Task PutAsync(string key, byte[] content, CancellationToken ct);

    Task<byte[]?> GetAsync(string key, CancellationToken ct);

    /// <summary>Removes every object under the tenant's prefix (approved deletion, LFC-004). Returns how many objects were removed.</summary>
    Task<int> DeleteTenantAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>What a download link points at (DEC-061).</summary>
public enum DownloadKind
{
    Attachment, Export,
}

/// <summary>
/// Short-lived download tokens (TEN-005, section 18): bound to tenant, resource kind and id, the requesting subject and
/// an expiry at most 15 minutes ahead.
/// </summary>
public interface IDownloadLinks
{
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(15);

    string Issue(Guid tenantId, DownloadKind kind, Guid resourceId, string subject, DateTimeOffset expiresAt);

    bool IsValid(string token, Guid tenantId, DownloadKind kind, Guid resourceId, string subject, DateTimeOffset now);
}

/// <summary>Malware scan of an uploaded file (section 06). Development uses a visible simulator (AGT-004).</summary>
public interface IAttachmentScanner
{
    Task<bool> IsCleanAsync(byte[] content, CancellationToken ct);
}
