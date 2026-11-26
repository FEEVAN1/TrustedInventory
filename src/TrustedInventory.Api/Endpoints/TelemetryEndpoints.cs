using Microsoft.AspNetCore.Mvc;
using TrustedInventory.Api.Http;
using TrustedInventory.Api.Security;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Inbound;
using TrustedInventory.Domain.Alerts;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Api.Endpoints;

/// <summary>Devices, readings, excursions and tenant alerts (docs/openapi.yaml tags Telemetry and Alerts, DEC-077).</summary>
public static class TelemetryEndpoints
{
    public static RouteGroupBuilder MapTelemetryEndpoints(this RouteGroupBuilder tenant)
    {
        var devices = tenant.MapGroup("/devices");
        devices.MapGet("", (TelemetryService svc, [AsParameters] ListQuery q, [FromQuery] bool? active, [FromQuery] Guid? zoneId, [FromQuery] bool? stale,
                CancellationToken ct) => svc.ListDevicesAsync(new DeviceFilter(active, zoneId, stale), q.ToPage(), ct))
            .WithName("listDevices").RequirePermission(Permissions.AlertsRead);
        devices.MapPost("", async (HttpContext http, TelemetryService svc, CreateDeviceRequest body, CancellationToken ct) =>
            {
                var device = await svc.CreateDeviceAsync(body, ct);
                Conventions.SetETag(http, device.Version);
                return Results.Created($"/api/v1/devices/{device.Id}", device);
            })
            .WithName("createDevice").RequirePermission(Permissions.DevicesManage);
        devices.MapGet("/{id:guid}", async (HttpContext http, TelemetryService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetDeviceAsync(id, ct)))
            .WithName("getDevice").RequirePermission(Permissions.AlertsRead);
        devices.MapPatch("/{id:guid}", async (HttpContext http, TelemetryService svc, Guid id, UpdateDeviceRequest body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.UpdateDeviceAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("updateDevice").RequirePermission(Permissions.DevicesManage);
        devices.MapPost("/{id:guid}/retire", async (HttpContext http, TelemetryService svc, Guid id, CancellationToken ct) =>
                Conventions.Retired(http, await svc.RetireDeviceAsync(id, Conventions.IfMatch(http), ct)))
            .WithName("retireDevice").RequirePermission(Permissions.DevicesManage);
        devices.MapPost("/{id:guid}/rotate-credential", async (HttpContext http, TelemetryService svc, Guid id, CancellationToken ct) =>
            {
                var (credential, version) = await svc.RotateCredentialAsync(id, Conventions.IfMatch(http), ct);
                Conventions.SetETag(http, version);
                return Results.Ok(credential);
            })
            .WithName("rotateDeviceCredential").RequirePermission(Permissions.DevicesManage);

        tenant.MapGet("/telemetry/readings", (TelemetryService svc, [AsParameters] ListQuery q, [FromQuery] Guid? deviceId, [FromQuery] Guid? zoneId,
                [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct) =>
                svc.ListReadingsAsync(new ReadingFilter(deviceId, zoneId, from, to), q.ToPage(), ct))
            .WithName("listReadings").RequirePermission(Permissions.AlertsRead);

        var excursions = tenant.MapGroup("/excursions");
        excursions.MapGet("", (ExcursionService svc, [AsParameters] ListQuery q, [FromQuery] ExcursionStatus? status, [FromQuery] Guid? zoneId,
                [FromQuery] Guid? warehouseId, CancellationToken ct) => svc.ListAsync(new ExcursionFilter(status, zoneId, warehouseId), q.ToPage(), ct))
            .WithName("listExcursions").RequirePermission(Permissions.AlertsRead);
        excursions.MapGet("/{id:guid}", async (HttpContext http, ExcursionService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getExcursion").RequirePermission(Permissions.AlertsRead);
        excursions.MapPost("/{id:guid}/acknowledge", async (HttpContext http, ExcursionService svc, Guid id, AcknowledgeRequest body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.AcknowledgeAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("acknowledgeExcursion").RequirePermission(Permissions.AlertsAcknowledge);
        excursions.MapPost("/{id:guid}/resolve", async (HttpContext http, ExcursionService svc, Guid id, ResolveExcursionRequest body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.ResolveAsync(Conventions.IdempotencyKey(http), id, Conventions.IfMatch(http), body, ct)))
            .WithName("resolveExcursion").RequirePermission(Permissions.QualityHold);

        var alerts = tenant.MapGroup("/alerts");
        alerts.MapGet("", (AlertService svc, [AsParameters] ListQuery q, [FromQuery] AlertType? type, [FromQuery] AlertStatus? status,
                [FromQuery] AlertSeverity? severity, [FromQuery] Guid? warehouseId, [FromQuery] Guid? zoneId, CancellationToken ct) =>
                svc.ListAsync(new AlertFilter(type, status, severity, warehouseId, zoneId), q.ToPage(), ct))
            .WithName("listAlerts").RequirePermission(Permissions.AlertsRead);
        alerts.MapGet("/{id:guid}", async (HttpContext http, AlertService svc, Guid id, CancellationToken ct) => Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getAlert").RequirePermission(Permissions.AlertsRead);
        alerts.MapPost("/{id:guid}/acknowledge", async (HttpContext http, AlertService svc, Guid id, AcknowledgeRequest body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.AcknowledgeAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("acknowledgeAlert").RequirePermission(Permissions.AlertsAcknowledge);
        // Lot traceability (section 02, DEC-078).
        tenant.MapGet("/traceability/lots/{id:guid}", (TraceabilityService svc, Guid id, CancellationToken ct) => svc.GetAsync(id, ct))
            .WithName("getLotTraceability").RequirePermission(Permissions.StockRead);
        return tenant;
    }

    /// <summary>
    /// POST /telemetry/readings (TEL-001): device credential only, no user token and no tenant header. The service authenticates the
    /// bearer as a device credential and derives tenant and zone from the device.
    /// </summary>
    public static RouteGroupBuilder MapTelemetryIngestion(this RouteGroupBuilder api)
    {
        api.MapPost("/telemetry/readings", async (HttpContext http, IDeviceGateway gateway, TelemetryService svc, CancellationToken ct) =>
            {
                var header = http.Request.Headers.Authorization.ToString();
                var bearer = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
                var device = await gateway.AuthenticateAsync(bearer, ct)
                    ?? throw new AppException(ErrorCode.Unauthenticated, "A valid device credential is required.");
                // Authentication first: an unauthenticated caller learns nothing about the body (401 before 400).
                ReadingRequest? body;
                try
                {
                    body = await http.Request.ReadFromJsonAsync<ReadingRequest>(ct);
                }
                catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
                {
                    throw new AppException(ErrorCode.MalformedRequest, "The reading body is not valid JSON.");
                }
                if (body is null)
                    throw new AppException(ErrorCode.MalformedRequest, "A reading body is required.");
                return Results.Ok(await svc.IngestAsync(device, body, ct));
            })
            .WithName("ingestReading")
            .AllowAnonymous();
        return api;
    }
}
