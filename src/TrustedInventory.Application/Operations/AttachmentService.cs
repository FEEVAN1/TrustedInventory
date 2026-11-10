using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Saas;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Operations;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Operations;

public sealed record AttachmentDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, string FileName, string MediaType, long Size, string Digest,
    AttachmentScanStatus ScanStatus, string? EntityType, Guid? EntityId) : IVersioned;

public sealed record DownloadLinkDto(string Url, DateTimeOffset ExpiresAt);

public sealed record UploadedFile(string FileName, string? DeclaredMediaType, byte[] Content, string? EntityType, Guid? EntityId);

public sealed record DownloadedFile(string FileName, string MediaType, byte[] Content);

/// <summary>
/// Evidence attachments (TEN-005, DEC-022, DEC-061): content-checked PDF, JPEG or PNG up to 10 MiB under the tenant prefix,
/// counted against the attachment_bytes quota, scanned before any download, and served only through short-lived links.
/// </summary>
public sealed class AttachmentService(ITenantUnitOfWork uow, ICallerAccessor callers, IQuotaGate quota, IObjectStore store, IAttachmentScanner scanner,
    IDownloadLinks links)
{
    public async Task<AttachmentDto> UploadAsync(UploadedFile file, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.AdjustmentsRequest);
        caller.RequireMutable();
        var name = Path.GetFileName(file.FileName ?? "").Trim();
        if (name.Length is < 1 or > 255)
            throw AppException.Validation("file", "Give the file a name of 1 to 255 characters.");
        if (file.Content.Length is 0 or > (int)Attachment.MaxBytes)
            throw AppException.Validation("file", "Attachments are 1 byte to 10 MiB.");
        var mediaType = Sniff(file.Content) ?? throw AppException.Validation("file", "Only PDF, JPEG and PNG files are accepted.");
        if (file.DeclaredMediaType is { Length: > 0 } declared && declared != "application/octet-stream" && declared != mediaType)
            throw AppException.Validation("file", $"The file content is {mediaType}, not {declared}.");
        if ((file.EntityType is null) != (file.EntityId is null) || (file.EntityType is not null && file.EntityType != "Adjustment"))
            throw AppException.Validation("entityType", "Attachments bind to an adjustment (entityType Adjustment with its entityId) or to nothing yet.");
        var clean = await scanner.IsCleanAsync(file.Content, ct); // simulated scan (DEC-061)
        var digest = SHA256.HashData(file.Content);

        return await uow.WriteAsync(async (s, c) =>
        {
            if (file.EntityId is { } adjustmentId)
            {
                var adjustment = await s.Db.Set<Adjustment>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == adjustmentId, c);
                if (adjustment is null || !adjustment.IsOpen || !caller.CanAccess(Permissions.AdjustmentsRequest, adjustment.WarehouseId))
                    throw AppException.Validation("entityId", "Bind evidence to an open adjustment you may change.");
            }
            await quota.ReserveAsync(s, QuotaKey.AttachmentBytes, file.Content.LongLength, c);
            var attachment = DomainGuard.Run(() => Attachment.Create(Guid.CreateVersion7(), caller.TenantId, name, mediaType, file.Content.LongLength, digest,
                clean, file.EntityType, file.EntityId, caller.Subject, s.Now));
            await store.PutAsync(attachment.ObjectKey, file.Content, c);
            s.Db.Add(attachment);
            var dto = Dto(attachment);
            s.Audit(new AuditEntry("AttachmentUploaded", "Attachment", attachment.Id, attachment.Version, null, null, dto));
            await s.Db.SaveChangesAsync(c);
            return dto;
        }, ct);
    }

    public Task<AttachmentDto> GetAsync(Guid id, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
            Dto(await s.Db.Set<Attachment>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, c) ?? throw AppException.NotFound("Attachment")), ct);
    }

    /// <summary>Only clean files get a link, valid 15 minutes for this caller (TEN-005, DEC-061).</summary>
    public Task<DownloadLinkDto> LinkAsync(Guid id, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var attachment = await s.Db.Set<Attachment>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, c) ?? throw AppException.NotFound("Attachment");
            if (attachment.ScanStatus != AttachmentScanStatus.Clean)
                throw AppException.InvalidState($"The file is {attachment.ScanStatus}; only clean files can be downloaded.");
            var expires = s.Now + IDownloadLinks.MaxLifetime;
            var token = links.Issue(caller.TenantId, DownloadKind.Attachment, id, caller.Subject, expires);
            return new DownloadLinkDto($"/api/v1/attachments/{id}/download?token={Uri.EscapeDataString(token)}", expires);
        }, ct);
    }

    public Task<DownloadedFile> DownloadAsync(Guid id, string token, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            if (!links.IsValid(token, caller.TenantId, DownloadKind.Attachment, id, caller.Subject, s.Now))
                throw AppException.NotFound("Download");
            var attachment = await s.Db.Set<Attachment>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, c) ?? throw AppException.NotFound("Download");
            if (attachment.ScanStatus != AttachmentScanStatus.Clean)
                throw AppException.NotFound("Download");
            var content = await store.GetAsync(attachment.ObjectKey, c) ?? throw AppException.NotFound("Download");
            return new DownloadedFile(attachment.FileName, attachment.MediaType, content);
        }, ct);
    }

    /// <summary>The type comes from the leading bytes, never from the name or the declared header.</summary>
    internal static string? Sniff(byte[] content) =>
        content.AsSpan().StartsWith("%PDF-"u8) ? "application/pdf"
        : content.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }) ? "image/jpeg"
        : content.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? "image/png"
        : null;

    private static AttachmentDto Dto(Attachment a) => new(a.Id, a.Version, a.CreatedAt, a.CreatedBy, a.UpdatedAt, a.FileName, a.MediaType, a.SizeBytes,
        Convert.ToHexStringLower(a.DigestSha256), a.ScanStatus, a.EntityType, a.EntityId);
}
