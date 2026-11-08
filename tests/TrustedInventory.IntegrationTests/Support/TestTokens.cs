using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace TrustedInventory.IntegrationTests.Support;

/// <summary>
/// Test identity provider: signs RS256 tokens that the API validates exactly as production tokens
/// (issuer, audience, lifetime, signature, MFA claim). Simulated; never used outside tests (AGT-004).
/// </summary>
public static class TestTokens
{
    public const string Issuer = "https://idp.test.trustedinventory.invalid";
    public const string Audience = "trustedinventory-api";

    private static readonly RSA Rsa = RSA.Create(2048);
    public static readonly RsaSecurityKey SigningKey = new(Rsa) { KeyId = "test-key-1" };

    public static string Issue(string subject, bool mfa = true, DateTimeOffset? authTime = null, string issuer = Issuer,
        string audience = Audience, TimeSpan? lifetime = null, SecurityKey? key = null, string? email = null, bool emailVerified = true)
    {
        var now = DateTimeOffset.UtcNow;
        var claims = new List<Claim>
        {
            new("sub", subject),
            new("auth_time", (authTime ?? now).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new("amr", "pwd"),
            // Synthetic address per subject; signup reads email and email_verified from the token (ONB-001).
            new("email", email ?? $"{subject}@example.invalid"),
            new("email_verified", emailVerified ? "true" : "false", ClaimValueTypes.Boolean),
        };
        if (mfa)
            claims.Add(new Claim("amr", "otp"));

        // Not-before sits well in the past so tests can also issue already-expired tokens.
        var token = new JwtSecurityToken(issuer, audience, claims, now.AddMinutes(-15).UtcDateTime,
            now.Add(lifetime ?? TimeSpan.FromMinutes(10)).UtcDateTime,
            new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
