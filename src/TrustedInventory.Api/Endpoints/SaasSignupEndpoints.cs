using TrustedInventory.Api.Http;
using TrustedInventory.Api.Security;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Saas;

namespace TrustedInventory.Api.Endpoints;

/// <summary>
/// Signup, provisioning status and the public plan catalog (docs/openapi.yaml tags Signup, Plans). These routes
/// run before any tenant exists, so they sit outside the tenant group: the identity comes from the token.
/// </summary>
public static class SaasSignupEndpoints
{
    public static RouteGroupBuilder MapSaasSignupEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/saas/signup", async (HttpContext http, SignupService svc, SignupRequest body, CancellationToken ct) =>
            {
                var accepted = await svc.SignupAsync(Identity(http), IdempotencyKey(http), body, ct);
                return Results.Accepted($"/api/v1/saas/provisioning/{accepted.ProvisioningJobId}", accepted);
            })
            .WithName("signup")
            .RequirePermission(IdentityEndpoints.Authenticated);

        api.MapGet("/saas/provisioning/{id:guid}", (HttpContext http, SignupService svc, Guid id, CancellationToken ct) =>
                svc.GetJobAsync(Subject(http), id, ct))
            .WithName("getProvisioningJob")
            .RequirePermission(IdentityEndpoints.Authenticated);

        // Public catalog (section 28): no token, no tenant. Rate limiting still applies by client address.
        api.MapGet("/saas/plans", (PlanCatalogService svc, [AsParameters] ListQuery q, CancellationToken ct) =>
                svc.ListAsync(q.ToPage(), ct))
            .WithName("listPlans")
            .AllowAnonymous();
        return api;
    }

    private static string Subject(HttpContext http) =>
        http.User.FindFirst("sub")?.Value ?? throw new AppException(ErrorCode.Unauthenticated, "The token has no subject.");

    /// <summary>Email and verification come only from the validated token (ONB-001).</summary>
    private static SignupIdentity Identity(HttpContext http) =>
        new(Subject(http), http.User.FindFirst("email")?.Value,
            string.Equals(http.User.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>Signup is a command that must not run twice (STK-007 conventions): a UUID Idempotency-Key is required.</summary>
    private static Guid IdempotencyKey(HttpContext http) =>
        http.Request.Headers["Idempotency-Key"] is { Count: 1 } values && Guid.TryParse(values[0], out var key)
            ? key
            : throw new AppException(ErrorCode.IdempotencyKeyRequired, "A UUID Idempotency-Key header is required.");
}
