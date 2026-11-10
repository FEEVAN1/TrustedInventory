using Microsoft.AspNetCore.Mvc;
using TrustedInventory.Api.Http;
using TrustedInventory.Api.Security;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Operations;
using TrustedInventory.Domain.Operations;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Api.Endpoints;

/// <summary>Attachments and exports (docs/openapi.yaml tags Attachments and Exports, DEC-061). Handlers only translate HTTP.</summary>
public static class FileEndpoints
{
    public static RouteGroupBuilder MapFileEndpoints(this RouteGroupBuilder tenant)
    {
        var attachments = tenant.MapGroup("/attachments");
        // The form is read inside the handler, after authentication, tenant, permission and access checks: binding it as a
        // parameter would reject non-multipart requests before those run.
        attachments.MapPost("", async (HttpContext http, AttachmentService svc, CancellationToken ct) =>
            {
                if (!http.Request.HasFormContentType)
                    throw AppException.Validation("file", "Send the file as multipart/form-data.");
                var form = await http.Request.ReadFormAsync(ct);
                var file = form.Files.GetFile("file");
                var entityType = form["entityType"].FirstOrDefault() is { Length: > 0 } type ? type : null;
                Guid? entityId = Guid.TryParse(form["entityId"].FirstOrDefault(), out var parsed) ? parsed : null;
                if (file is null)
                    throw AppException.Validation("file", "Send the file as multipart form field 'file'.");
                if (file.Length > Attachment.MaxBytes)
                    throw AppException.Validation("file", "Attachments are 1 byte to 10 MiB.");
                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, ct);
                var created = await svc.UploadAsync(new UploadedFile(file.FileName, file.ContentType, buffer.ToArray(), entityType, entityId), ct);
                return Conventions.Created(http, $"/api/v1/attachments/{created.Id}", created);
            })
            .DisableAntiforgery().WithName("uploadAttachment").RequirePermission(Permissions.AdjustmentsRequest);
        attachments.MapGet("/{id:guid}", async (HttpContext http, AttachmentService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getAttachment").RequirePermission(Permissions.StockRead);
        attachments.MapGet("/{id:guid}/download-link", async (AttachmentService svc, Guid id, CancellationToken ct) =>
                Results.Ok(await svc.LinkAsync(id, ct)))
            .WithName("getAttachmentDownloadLink").RequirePermission(Permissions.StockRead);
        attachments.MapGet("/{id:guid}/download", async (AttachmentService svc, Guid id, [FromQuery] string token, CancellationToken ct) =>
            {
                var file = await svc.DownloadAsync(id, token, ct);
                return Results.File(file.Content, file.MediaType, file.FileName);
            })
            .WithName("downloadAttachment").RequirePermission(Permissions.StockRead);

        var exports = tenant.MapGroup("/exports");
        exports.MapGet("", (ExportService svc, [AsParameters] ListQuery q, [FromQuery] ExportType? type, [FromQuery] ExportStatus? status, CancellationToken ct) =>
                svc.ListAsync(new ExportFilter(type, status), q.ToPage(), ct))
            .WithName("listExports").RequirePermission(Permissions.ExportsCreate);
        exports.MapPost("", async (HttpContext http, ExportService svc, CreateExport body, CancellationToken ct) =>
            {
                var job = await svc.CreateAsync(body, ct);
                http.Response.Headers.ETag = $"\"{job.Version}\"";
                return Results.Accepted($"/api/v1/exports/{job.Id}", job);
            })
            .WithName("createExport").RequirePermission(Permissions.ExportsCreate);
        exports.MapGet("/{id:guid}", async (HttpContext http, ExportService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getExport").RequirePermission(Permissions.ExportsCreate);
        exports.MapGet("/{id:guid}/download-link", async (ExportService svc, Guid id, CancellationToken ct) =>
                Results.Ok(await svc.LinkAsync(id, ct)))
            .WithName("getExportDownloadLink").RequirePermission(Permissions.ExportsCreate);
        exports.MapGet("/{id:guid}/download", async (ExportService svc, Guid id, [FromQuery] string token, CancellationToken ct) =>
            {
                var file = await svc.DownloadAsync(id, token, ct);
                return Results.File(file.Content, file.MediaType, file.FileName);
            })
            .WithName("downloadExport").RequirePermission(Permissions.ExportsCreate);
        return tenant;
    }
}
