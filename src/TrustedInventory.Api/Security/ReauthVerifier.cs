using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using TrustedInventory.Application.Administration;
using TrustedInventory.Application.Errors;

namespace TrustedInventory.Api.Security;

/// <summary>
/// DEC-009 re-authentication check. The token must be signed by the configured identity provider (same issuer and
/// signing keys as bearer tokens), carry an accepted audience (<c>Auth:ReauthAudiences</c>, defaulting to the API
/// audience; production adds the PWA client id because ID tokens are addressed to the client), name the caller as
/// subject, show <c>auth_time</c> no older than 300 seconds, and show MFA. Only its SHA-256 is kept.
/// </summary>
public sealed class JwtReauthVerifier(
    IOptionsMonitor<JwtBearerOptions> bearerOptions,
    MfaOptions mfa,
    TimeProvider clock,
    IConfiguration configuration) : IReauthVerifier
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan FutureSkew = TimeSpan.FromSeconds(60);

    public async Task<byte[]> VerifyAsync(string reauthToken, string expectedSubject, CancellationToken ct)
    {
        var options = bearerOptions.Get(JwtBearerDefaults.AuthenticationScheme);
        var metadata = options.Configuration
            ?? (options.ConfigurationManager is { } manager ? await manager.GetConfigurationAsync(ct) : null)
            ?? throw Reauth("The identity provider configuration is unavailable.");

        var parameters = options.TokenValidationParameters.Clone();
        parameters.IssuerSigningKeys = metadata.SigningKeys;
        var audiences = configuration.GetSection("Auth:ReauthAudiences").Get<string[]>() is { Length: > 0 } configured
            ? configured
            : [options.Audience ?? parameters.ValidAudience];
        parameters.ValidAudience = null;
        parameters.ValidAudiences = audiences;

        var result = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(reauthToken, parameters);
        if (!result.IsValid)
            throw Reauth("The re-authentication token is not valid.");

        var identity = result.ClaimsIdentity;
        if (identity.FindFirst("sub")?.Value != expectedSubject)
            throw Reauth("The re-authentication token belongs to a different user.");

        var now = clock.GetUtcNow();
        if (!long.TryParse(identity.FindFirst("auth_time")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            throw Reauth("Sign in again: the token does not show when you authenticated.");
        var authTime = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (now - authTime > MaxAge || authTime - now > FutureSkew)
            throw Reauth("Sign in again: re-authentication must be less than five minutes old.");

        var hasMfa = identity.FindAll("amr").Any(c => mfa.AcceptedAmr.Contains(c.Value, StringComparer.Ordinal))
            || identity.FindAll("acr").Any(c => mfa.AcceptedAcr.Contains(c.Value, StringComparer.Ordinal));
        if (!hasMfa)
            throw Reauth("Re-authentication must use multi-factor authentication.");

        return SHA256.HashData(Encoding.UTF8.GetBytes(reauthToken));
    }

    private static AppException Reauth(string detail) => new(ErrorCode.ReauthRequired, detail);
}
