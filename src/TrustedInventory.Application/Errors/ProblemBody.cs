using System.Text.Json.Nodes;

namespace TrustedInventory.Application.Errors;

/// <summary>
/// Error body (section 15): {type, title, status, code, detail, traceId, errors[]}. Built here so the API and
/// stored idempotent rejections produce the same shape. Never includes SQL, secrets or foreign identifiers.
/// </summary>
public static class ProblemBody
{
    public static JsonObject From(AppException ex, string? traceId)
    {
        var body = Create(ex.Status, ex.Code, ex.Message, traceId, ex.Errors);
        if (ex.Extensions is { } extra)
            foreach (var (key, value) in extra)
                body[key] = value?.DeepClone();
        return body;
    }

    public static JsonObject Create(int status, ErrorCode code, string detail, string? traceId, IReadOnlyList<FieldError>? errors = null)
    {
        var wire = ErrorCodes.Wire(code);
        var body = new JsonObject
        {
            ["type"] = $"https://errors.trustedinventory.example/{wire.ToLowerInvariant().Replace('_', '-')}",
            ["title"] = Title(status),
            ["status"] = status,
            ["code"] = wire,
            ["detail"] = detail,
            ["traceId"] = traceId,
        };
        if (errors is { Count: > 0 })
        {
            body["errors"] = new JsonArray(errors.Select(e => (JsonNode)new JsonObject
            {
                ["field"] = e.Field,
                ["code"] = ErrorCodes.Wire(e.Code),
                ["message"] = e.Message,
            }).ToArray());
        }
        return body;
    }

    private static string Title(int status) => status switch
    {
        400 => "Bad request",
        401 => "Authentication required",
        403 => "Forbidden",
        404 => "Not found",
        409 => "Conflict",
        412 => "Precondition failed",
        422 => "Unprocessable request",
        428 => "Precondition required",
        429 => "Too many requests",
        503 => "Service unavailable",
        _ => "Error",
    };
}
