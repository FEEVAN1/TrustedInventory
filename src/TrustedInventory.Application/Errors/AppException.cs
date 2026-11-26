namespace TrustedInventory.Application.Errors;

/// <summary>Error codes from section 15 / docs/enums.md. On the wire they are UPPER_SNAKE (see <see cref="ErrorCodes.Wire"/>).</summary>
public enum ErrorCode
{
    MalformedRequest,
    InvalidCursor,
    IdempotencyKeyRequired,
    Unauthenticated,
    Forbidden,
    ReauthRequired,
    NotFound,
    InsufficientStock,
    StockBlocked,
    InvalidState,
    IdempotencyMismatch,
    ApprovalStale,
    ApprovalSelf,
    CountLocked,
    RetryableConflict,
    VersionConflict,
    PreconditionRequired,
    ValidationFailed,
    OverReceipt,
    IncompatibleBin,
    BarcodeConflict,
    UnknownBarcode,
    MissingMedicalEvidence,
    ReleaseIneligible,
    DuplicateSerial,
    ReturnExceedsShipped,
    RateLimited,
    ServiceUnavailable,
    SubscriptionRestricted,
    TenantSecuritySuspended,
    FeatureNotEnabled,
    TrialNotEligible,
    PlanLimitReached,
    DowngradeBlocked,
    RetentionBlocked,
}

public static class ErrorCodes
{
    /// <summary>Wire spelling, e.g. VersionConflict → VERSION_CONFLICT.</summary>
    public static string Wire(ErrorCode code) => System.Text.Json.JsonNamingPolicy.SnakeCaseUpper.ConvertName(code.ToString());
}

public sealed record FieldError(string Field, ErrorCode Code, string Message);

/// <summary>An expected failure with a wire error code. Anything else is a 500 with no internals exposed.</summary>
public class AppException(ErrorCode code, string detail, IReadOnlyList<FieldError>? errors = null) : Exception(detail)
{
    public ErrorCode Code { get; } = code;
    public IReadOnlyList<FieldError> Errors { get; } = errors ?? [];

    /// <summary>Optional contract fields added to the error body: quota, feature, recovery, blockers (DEC-041).</summary>
    public System.Text.Json.Nodes.JsonObject? Extensions { get; init; }

    /// <summary>For RATE_LIMITED: how long until a retry can succeed (sent as Retry-After).</summary>
    public TimeSpan? RetryAfter { get; init; }

    public int Status => StatusOf(Code);

    public static AppException RateLimited(string detail, TimeSpan retryAfter) =>
        new(ErrorCode.RateLimited, detail) { RetryAfter = retryAfter };

    public static int StatusOf(ErrorCode code) => code switch
    {
        ErrorCode.MalformedRequest or ErrorCode.InvalidCursor or ErrorCode.IdempotencyKeyRequired => 400,
        ErrorCode.Unauthenticated => 401,
        ErrorCode.Forbidden or ErrorCode.ReauthRequired or ErrorCode.SubscriptionRestricted or ErrorCode.TenantSecuritySuspended
            or ErrorCode.FeatureNotEnabled => 403,
        ErrorCode.NotFound => 404,
        ErrorCode.VersionConflict => 412,
        ErrorCode.PreconditionRequired => 428,
        ErrorCode.ValidationFailed or ErrorCode.OverReceipt or ErrorCode.IncompatibleBin or ErrorCode.BarcodeConflict
            or ErrorCode.UnknownBarcode or ErrorCode.MissingMedicalEvidence or ErrorCode.ReleaseIneligible or ErrorCode.DuplicateSerial
            or ErrorCode.ReturnExceedsShipped => 422,
        ErrorCode.RateLimited => 429,
        ErrorCode.ServiceUnavailable => 503,
        _ => 409,
    };

    public static AppException NotFound(string what) => new(ErrorCode.NotFound, $"{what} was not found.");

    /// <summary>Same response whether the target is missing or outside the caller's scope (TEN isolation evidence).</summary>
    public static AppException Forbidden(string detail = "You do not have permission for this action.") =>
        new(ErrorCode.Forbidden, detail);

    public static AppException InvalidState(string detail) => new(ErrorCode.InvalidState, detail);

    public static AppException VersionConflict() =>
        new(ErrorCode.VersionConflict, "The resource was changed by someone else. Reload it and retry.");

    public static AppException Validation(string field, string message) =>
        new(ErrorCode.ValidationFailed, "The request has invalid fields.", [new FieldError(field, ErrorCode.ValidationFailed, message)]);
}
