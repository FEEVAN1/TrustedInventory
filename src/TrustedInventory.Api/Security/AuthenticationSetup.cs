using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using TrustedInventory.Api.Http;
using TrustedInventory.Application.Errors;
using TrustedInventory.Infrastructure;

namespace TrustedInventory.Api.Security;

public static class AuthenticationSetup
{
    public const string HumanPolicy = "HumanWithMfa";

    /// <summary>
    /// OIDC bearer tokens (IAM-002): issuer, audience, lifetime and signing keys are all validated, and human
    /// callers must present an MFA authentication method. Settings are required; startup fails without them.
    /// </summary>
    public static IServiceCollection AddApiAuthentication(this IServiceCollection services, IConfiguration config)
    {
        var authority = DependencyInjection.Required(config, "Auth:Authority");
        var audience = DependencyInjection.Required(config, "Auth:Audience");
        var mfa = new MfaOptions(
            config.GetSection("Auth:Mfa:AcceptedAmr").Get<string[]>() ?? ["mfa", "otp", "hwk", "swk"],
            config.GetSection("Auth:Mfa:AcceptedAcr").Get<string[]>() ?? []);
        services.AddSingleton(mfa);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.Authority = authority;
                // Optional: where to read discovery and signing keys when the API reaches the provider by a different address
                // than the issuer it names (the Compose network, DEC-105). The issuer check below is unchanged: tokens must
                // still name Auth:Authority exactly.
                if (config["Auth:MetadataAddress"] is { Length: > 0 } metadata)
                    o.MetadataAddress = metadata;
                o.Audience = audience;
                o.MapInboundClaims = false;
                o.RequireHttpsMetadata = config.GetValue("Auth:RequireHttpsMetadata", true);
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = authority,
                    ValidAudience = audience,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub",
                };
                o.Events = new JwtBearerEvents
                {
                    OnChallenge = async ctx =>
                    {
                        ctx.HandleResponse();
                        await Problems.WriteAsync(ctx.HttpContext, 401, ErrorCode.Unauthenticated, "A valid bearer token is required.");
                    },
                    OnForbidden = ctx =>
                        Problems.WriteAsync(ctx.HttpContext, 403, ErrorCode.Forbidden,
                            "You do not have permission for this action. Sign-in must use multi-factor authentication."),
                };
            });

        services.AddSingleton<IAuthorizationHandler, MfaHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(HumanPolicy, p => p.RequireAuthenticatedUser().RequireClaim("sub").AddRequirements(new MfaRequirement()))
            .SetDefaultPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireClaim("sub")
                .AddRequirements(new MfaRequirement()).Build())
            .SetFallbackPolicy(null);
        return services;
    }
}

public sealed record MfaOptions(IReadOnlyList<string> AcceptedAmr, IReadOnlyList<string> AcceptedAcr);

public sealed class MfaRequirement : IAuthorizationRequirement;

/// <summary>Human tokens must show MFA through an accepted <c>amr</c> value or <c>acr</c> level (IAM-002).</summary>
public sealed class MfaHandler(MfaOptions options) : AuthorizationHandler<MfaRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, MfaRequirement requirement)
    {
        var user = context.User;
        if (user.FindAll("amr").Any(c => options.AcceptedAmr.Contains(c.Value, StringComparer.Ordinal))
            || user.FindAll("acr").Any(c => options.AcceptedAcr.Contains(c.Value, StringComparer.Ordinal)))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
