using Microsoft.AspNetCore.Mvc;
using TrustedInventory.Api.Http;
using TrustedInventory.Api.Security;
using TrustedInventory.Application.Audit;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Api.Endpoints;

/// <summary>Audit trail and chain verification (tag Audit; AUD-001..003, OPS-001).</summary>
public static class AuditEndpoints
{
    public static RouteGroupBuilder MapAuditEndpoints(this RouteGroupBuilder api)
    {
        var audit = api.MapGroup("/audit");
        audit.MapGet("/events", (AuditService svc, [AsParameters] ListQuery q, [FromQuery] DateTimeOffset? from,
                [FromQuery] DateTimeOffset? to, [FromQuery] long? fromSequence, [FromQuery] long? toSequence,
                [FromQuery] string? objectType, [FromQuery] Guid? objectId, [FromQuery] string? actorSubject, CancellationToken ct) =>
                svc.ListAsync(new AuditEventFilter(from, to, fromSequence, toSequence, objectType, objectId, actorSubject), q.ToPage(), ct))
            .WithName("listAuditEvents").RequirePermission(Permissions.AuditRead);
        audit.MapPost("/verify", (AuditService svc, AuditVerifyRequest body, CancellationToken ct) => svc.VerifyAsync(body, ct))
            .WithName("verifyAuditChain").RequirePermission(Permissions.AuditRead);
        return api;
    }
}
