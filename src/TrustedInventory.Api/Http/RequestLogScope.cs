using System.Diagnostics;

namespace TrustedInventory.Api.Http;

/// <summary>
/// Opens the request's log scope (traceId and route name) and writes one completion line with method, route name, status and
/// duration (NFR-MONITOR, DEC-079). The path and query string are deliberately not logged: download links carry tokens there.
/// </summary>
public sealed partial class RequestLogScope(RequestDelegate next, ILogger<RequestLogScope> log)
{
    /// <summary>Set by the tenant middleware once membership is validated, so the completion line names the tenant.</summary>
    public const string TenantItem = "ti.tenantId";

    public async Task InvokeAsync(HttpContext http)
    {
        var route = http.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? "unmatched";
        using var scope = log.BeginScope(new Dictionary<string, object?> { ["traceId"] = Problems.TraceId(http), ["route"] = route });
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(http);
        }
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var tenant = http.Items.TryGetValue(TenantItem, out var t) ? t?.ToString() : null;
            LogCompleted(log, http.Request.Method, route, http.Response.StatusCode, elapsedMs, tenant ?? "none");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Method} {Route} answered {Status} in {ElapsedMs:0.0} ms for tenant {TenantId}")]
    private static partial void LogCompleted(ILogger logger, string method, string route, int status, double elapsedMs, string tenantId);
}
