using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;

namespace TrustedInventory.Api.Http;

/// <summary>Standard list query (API-004): limit, cursor, sort.</summary>
public sealed record ListQuery(
    [FromQuery(Name = "limit")] int? Limit,
    [FromQuery(Name = "cursor")] string? Cursor,
    [FromQuery(Name = "sort")] string? Sort)
{
    public PageRequest ToPage() => new(Limit, Cursor, Sort);
}

public static class Conventions
{
    /// <summary>
    /// Reads the required If-Match header (API-002): missing → 428 PRECONDITION_REQUIRED; the value must be a
    /// quoted positive integer ETag such as "3". A stale value is reported by the service as 412.
    /// </summary>
    public static int IfMatch(HttpContext http)
    {
        var values = http.Request.Headers.IfMatch;
        if (values.Count == 0)
            throw new AppException(ErrorCode.PreconditionRequired, "If-Match with the current ETag is required.");
        var raw = values.Count == 1 ? values[0]!.Trim() : "";
        if (raw.Length >= 3 && raw[0] == '"' && raw[^1] == '"'
            && int.TryParse(raw.AsSpan(1, raw.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            && version > 0)
            return version;
        throw new AppException(ErrorCode.MalformedRequest, "If-Match must be a single quoted version, e.g. \"3\".");
    }

    public static void SetETag(HttpContext http, int version) =>
        http.Response.Headers.ETag = $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>200 with the resource and its ETag.</summary>
    public static IResult Ok<T>(HttpContext http, T resource) where T : IVersioned
    {
        SetETag(http, resource.Version);
        return Results.Ok(resource);
    }

    /// <summary>201 with Location and ETag (API-003).</summary>
    public static IResult Created<T>(HttpContext http, string location, T resource) where T : IVersioned
    {
        SetETag(http, resource.Version);
        return Results.Created(location, resource);
    }

    /// <summary>Stock commands must not run twice (STK-007): a UUID Idempotency-Key header is required.</summary>
    public static Guid IdempotencyKey(HttpContext http) =>
        http.Request.Headers["Idempotency-Key"] is { Count: 1 } values && Guid.TryParse(values[0], out var key)
            ? key
            : throw new AppException(ErrorCode.IdempotencyKeyRequired, "A UUID Idempotency-Key header is required.");

    /// <summary>
    /// Writes a stored or fresh command outcome (STK-007): the original status and body on every replay, a problem
    /// body for rejections, and the resource version from the body as the ETag.
    /// </summary>
    public static IResult Outcome(HttpContext http, Application.Persistence.CommandOutcome outcome)
    {
        if (outcome.Status < 400 && outcome.Body?["version"] is { } version)
            SetETag(http, (int)version);
        return Results.Content(outcome.Body?.ToJsonString() ?? "null",
            outcome.Status >= 400 ? "application/problem+json" : "application/json", System.Text.Encoding.UTF8, outcome.Status);
    }

    /// <summary>204 for retirement, still returning the resulting version as the ETag (DEC-019).</summary>
    public static IResult Retired(HttpContext http, IVersioned resource)
    {
        SetETag(http, resource.Version);
        return Results.NoContent();
    }
}
