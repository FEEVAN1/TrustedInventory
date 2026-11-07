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
api.MapIdentityEndpoints();   // GET /me: authenticated, no tenant context
api.MapPlatformEndpoints();   // /platform/*: platform administrators, no tenant context

var tenant = api.MapGroup("").WithMetadata(TenantScopedMetadata.Instance);
tenant.MapLocationEndpoints();
tenant.MapCatalogEndpoints();
tenant.MapAdministrationEndpoints();
tenant.MapAuditEndpoints();

app.Run();

public partial class Program;
