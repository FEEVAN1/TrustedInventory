using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using TrustedInventory.Application.Errors;

namespace TrustedInventory.Api.Http;

/// <summary>Writes section 15 error bodies with content type application/problem+json.</summary>
public static partial class Problems
{
    public static string TraceId(HttpContext http) => Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier;

    public static Task WriteAsync(HttpContext http, int status, ErrorCode code, string detail, IReadOnlyList<FieldError>? errors = null)
    {
        if (http.Response.HasStarted)
            return Task.CompletedTask;
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/problem+json";
        return http.Response.WriteAsync(ProblemBody.Create(status, code, detail, TraceId(http), errors).ToJsonString());
    }

    /// <summary>
    /// Maps exceptions to error bodies. Unexpected exceptions become a bare 500 with only the trace id: no SQL,
    /// stack, secrets or identifiers from other tenants reach the client (section 15).
    /// </summary>
    public static async Task HandleAsync(HttpContext http)
    {
        var error = http.Features.Get<IExceptionHandlerFeature>()?.Error;
        switch (error)
        {
            case AppException app:
                if (app.Code == ErrorCode.RateLimited)
                    http.Response.Headers.RetryAfter = "60";
                if (!http.Response.HasStarted)
                {
                    http.Response.StatusCode = app.Status;
                    http.Response.ContentType = "application/problem+json";
                    await http.Response.WriteAsync(ProblemBody.From(app, TraceId(http)).ToJsonString());
                }
                break;
            case BadHttpRequestException { InnerException: JsonException json }:
                await WriteAsync(http, 400, ErrorCode.MalformedRequest, JsonDetail(json));
                break;
            case JsonException json:
                await WriteAsync(http, 400, ErrorCode.MalformedRequest, JsonDetail(json));
                break;
            case BadHttpRequestException bad:
                await WriteAsync(http, bad.StatusCode == 413 ? 413 : 400, ErrorCode.MalformedRequest, "The request could not be read.");
                break;
            default:
                LogUnhandled(http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Unhandled"),
                    error, http.Request.Method, http.Request.Path);
                http.Response.StatusCode = 500;
                http.Response.ContentType = "application/problem+json";
                await http.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    type = "about:blank",
                    title = "Internal server error",
                    status = 500,
                    traceId = TraceId(http),
                }));
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception? exception, string method, string path);

    // The JSON path (e.g. $.lines[0].quantity) helps the caller and contains no server internals.
    private static string JsonDetail(JsonException json) =>
        json.Path is { Length: > 0 } path ? $"Invalid or unexpected JSON at {path}." : "The request body is not valid JSON.";
}
