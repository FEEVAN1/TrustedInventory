using System.Threading.RateLimiting;
using TrustedInventory.Api.Security;

namespace TrustedInventory.Api.Http;

/// <summary>
/// Tenant-aware request budgets (SOP-001, DEC-073). Proposed runtime budgets until the owner approves them: 120 requests per minute
/// for each person in each tenant, 600 per minute for a tenant's service principals together, and a separate per-address budget
/// for anonymous calls (public plans, signup, webhooks). One noisy identity or tenant spends only its own budget, and a rejected
/// request is answered before any tenant context or database connection is opened (the limiter runs before
/// <see cref="TenantContextMiddleware"/>). Rejections are never counted as usage.
/// </summary>
public static class TenantRateLimits
{
    public const string ServiceClaim = "client_id";

    public static PartitionedRateLimiter<HttpContext> Create(IConfiguration config)
    {
        var human = config.GetValue("RateLimit:HumanPerMinute", 120);
        var service = config.GetValue("RateLimit:ServicePerMinute", 600);
        var anonymous = config.GetValue("RateLimit:PermitsPerMinute", 600);
        var identity = config.GetValue("RateLimit:PerIdentityPerMinute", 1200);
        var device = config.GetValue("RateLimit:DevicePerMinute", 120);
        var perTenant = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        {
            // Device ingestion has its own budget per credential (SOP-001: telemetry is configured separately from people).
            if (DeviceKey(http) is { } deviceKey)
                return Window(deviceKey, device);
            var (key, limit) = Partition(http, human, service, anonymous);
            return Window(key, limit);
        });
        // One overall budget per identity across all tenant headers, so cycling made-up tenant ids cannot buy fresh budgets
        // (each of those requests would otherwise reach the membership lookup).
        var perIdentity = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            http.User.FindFirst("sub")?.Value is { } subject ? Window($"id|{subject}", identity) : RateLimitPartition.GetNoLimiter("anon"));
        return PartitionedRateLimiter.CreateChained(perTenant, perIdentity);
    }

    /// <summary>"dev|&lt;credential id&gt;" for a device bearer on the ingestion route; the credential is verified later, so a forged one only
    /// spends its own made-up partition.</summary>
    public static string? DeviceKey(HttpContext http)
    {
        if (!HttpMethods.IsPost(http.Request.Method) || !http.Request.Path.StartsWithSegments("/api/v1/telemetry/readings"))
            return null;
        var header = http.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ti_dev_";
        return header.StartsWith(prefix, StringComparison.Ordinal) && header.Length >= prefix.Length + 32
            ? $"dev|{header.Substring(prefix.Length, 32)}"
            : "dev|invalid";
    }

    private static RateLimitPartition<string> Window(string key, int limit) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });

    /// <summary>
    /// The budget a request draws from. The tenant comes from the header before membership is checked, so every partition also
    /// carries the caller's own subject: a forged header moves a caller only into another partition of their own, never into
    /// another person's or tenant's budget. The service budget therefore applies per service identity in each tenant.
    /// </summary>
    public static (string Key, int Limit) Partition(HttpContext http, int human, int service, int anonymous)
    {
        var subject = http.User.FindFirst("sub")?.Value;
        if (subject is null)
            return ($"anon|{http.Connection.RemoteIpAddress}", anonymous);
        var tenant = http.Request.Headers[TenantContextMiddleware.TenantHeader].FirstOrDefault() is { } t && Guid.TryParse(t, out var id)
            ? id.ToString("N")
            : "none";
        return http.User.FindFirst(ServiceClaim) is not null
            ? ($"svc|{tenant}|{subject}", service)
            : ($"user|{tenant}|{subject}", human);
    }
}
