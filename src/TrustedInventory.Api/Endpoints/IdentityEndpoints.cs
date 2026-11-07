using System.Globalization;
using System.Security.Claims;
using TrustedInventory.Api.Security;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Organization;

namespace TrustedInventory.Api.Endpoints;

/// <summary>GET /me (docs/openapi.yaml tag Identity): any authenticated MFA caller, no tenant context.</summary>
public static class IdentityEndpoints
{
    public const string Authenticated = "authenticated";

    public static RouteGroupBuilder MapIdentityEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me", (HttpContext http, MeService svc, CancellationToken ct) =>
            {
                var user = http.User;
                var subject = user.FindFirst("sub")?.Value ?? throw new AppException(ErrorCode.Unauthenticated, "The token has no subject.");
                return svc.GetAsync(subject, user.FindFirst("name")?.Value, AuthTime(user), ct);
            })
            .WithName("getMe")
            .RequirePermission(Authenticated);
        return api;
    }

    /// <summary>Time of the user's last interactive authentication (OIDC auth_time), falling back to issue time.</summary>
    internal static DateTimeOffset AuthTime(ClaimsPrincipal user)
    {
        var raw = user.FindFirst("auth_time")?.Value ?? user.FindFirst("iat")?.Value;
        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : throw new AppException(ErrorCode.Unauthenticated, "The token has no authentication time.");
    }
}
