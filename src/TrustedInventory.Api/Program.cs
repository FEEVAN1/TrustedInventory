using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Json;
using TrustedInventory.Api.Endpoints;
using TrustedInventory.Api.Http;
using TrustedInventory.Api.Security;
using TrustedInventory.Application;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Security;
using TrustedInventory.Infrastructure;
using TrustedInventory.Infrastructure.Organization;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 1024 * 1024); // section 06 request size
builder.Services.Configure<JsonOptions>(o => JsonDefaults.Configure(o.SerializerOptions));
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddProblemDetails();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(config);
builder.Services.AddApiAuthentication(config);
builder.Services.AddScoped<HttpCallerAccessor>();
builder.Services.AddScoped<ICallerAccessor>(sp => sp.GetRequiredService<HttpCallerAccessor>());
builder.Services.AddTeamInfrastructure(config);
builder.Services.AddSingleton<TrustedInventory.Application.Administration.IReauthVerifier, JwtReauthVerifier>();

// Rate limit per authenticated principal, else per client address (section 06).
builder.Services.AddRateLimiter(o =>
{
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        RateLimitPartition.GetFixedWindowLimiter(
            http.User.FindFirst("sub")?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = config.GetValue("RateLimit:PermitsPerMinute", 600),
                Window = TimeSpan.FromMinutes(1),
            }));
    o.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "60";
        return new ValueTask(Problems.WriteAsync(ctx.HttpContext, 429, ErrorCode.RateLimited, "Too many requests. Retry later."));
    };
});

var allowedOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(allowedOrigins)
    .WithHeaders("Authorization", "Content-Type", "If-Match", "Idempotency-Key", TenantContextMiddleware.TenantHeader, "X-Correlation-Id")
    .WithExposedHeaders("ETag", "Location", "Retry-After")
    .WithMethods("GET", "POST", "PATCH")));

var app = builder.Build();

app.UseExceptionHandler(e => e.Run(Problems.HandleAsync));
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.Use(async (http, next) =>
{
    // The API serves JSON only: nothing may be framed, sniffed, cached or executed as a page.
    var h = http.Response.Headers;
    h.XContentTypeOptions = "nosniff";
    h.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
    h["Referrer-Policy"] = "no-referrer";
    h.CacheControl = "no-store";
    await next();
});
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.UseMiddleware<TenantContextMiddleware>();

// ARC-004: liveness and readiness; the only unauthenticated routes (API-001).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

var api = app.MapGroup("/api/v1").RequireAuthorization();
// Contract policies (x-access, x-entitlement, x-quota) attach by operationId after each endpoint is named.
((IEndpointConventionBuilder)api).Finally(OperationPolicyConvention.Apply);
api.MapIdentityEndpoints();   // GET /me: authenticated, no tenant context
api.MapSaasSignupEndpoints(); // signup, provisioning status, public plans: before any tenant exists
api.MapInvitationAcceptance(); // POST /invitations/accept: authenticated, not yet a member
app.MapPlatformEndpoints();   // /platform/v1/*: platform operations console, no tenant context (DEC-037)
app.MapPlatformBillingEndpoints(); // /platform/v1/plan-versions: plan catalog for platform billing (DEC-062)

var tenant = api.MapGroup("").WithMetadata(TenantScopedMetadata.Instance);
tenant.MapLocationEndpoints();
tenant.MapCatalogEndpoints();
tenant.MapAdministrationEndpoints();
tenant.MapAuditEndpoints();
tenant.MapTeamEndpoints();
tenant.MapInboundEndpoints();
tenant.MapOutboundEndpoints();
tenant.MapOperationsEndpoints();
tenant.MapFileEndpoints();
tenant.MapBillingEndpoints();

app.Run();

public partial class Program;
