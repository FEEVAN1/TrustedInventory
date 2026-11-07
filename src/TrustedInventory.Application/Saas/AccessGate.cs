using System.Text.Json.Nodes;
using TrustedInventory.Application.Errors;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Application.Saas;

/// <summary>Reads a tenant's stored access state through cp.tenant_access() (DEC-042).</summary>
public interface ITenantAccessReader
{
    Task<TenantAccessState?> ReadAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>
/// The lifecycle and entitlement steps of the request pipeline (SAS-005): access category against the state
/// matrix, then feature entitlement. Rejections name the reason and the recovery action, never disguising a
/// billing lock as anything else (section 29).
/// </summary>
public static class AccessGate
{
    public static void Enforce(TenantAccessState state, DateTimeOffset now, AccessCategory category, bool isOwner, string? feature)
    {
        var mode = state.Mode(now);
        if (!AccessMatrix.Allows(mode, category, isOwner))
            throw Rejection(state, now, mode, category);

        if (feature is not null && AccessMatrix.UsesEntitlements(category) && !state.HasFeature(feature, now))
        {
            throw new AppException(ErrorCode.FeatureNotEnabled, $"Your plan does not include '{feature}'.")
            {
                Extensions = new JsonObject
                {
                    ["feature"] = feature,
                    ["recovery"] = new JsonObject { ["action"] = "upgrade-plan" },
                },
            };
        }
    }

    public static AppException Rejection(TenantAccessState state, DateTimeOffset now, AccessMode mode, AccessCategory category)
    {
        var reasons = new JsonArray(state.Reasons(now).Select(r => (JsonNode)r.ToString()).ToArray());
        var (code, detail, action) = mode switch
        {
            AccessMode.Provisioning => (ErrorCode.InvalidState, "The workspace is still being set up.", "wait-for-provisioning"),
            AccessMode.SecuritySuspended => (ErrorCode.TenantSecuritySuspended,
                "This workspace is suspended for security review. Contact support.", "contact-support"),
            AccessMode.Closed => (ErrorCode.SubscriptionRestricted,
                "This workspace is closing; only export, closure status and safety actions remain.", "view-closure"),
            AccessMode.BillingRestricted => (ErrorCode.SubscriptionRestricted,
                "The subscription is not active. Reading, export and safety actions remain available.", "billing-recovery"),
            AccessMode.Deleted => (ErrorCode.Forbidden, "You do not have permission for this action.", "none"),
            _ => (ErrorCode.Forbidden, "This action is only available to the workspace owner.", "contact-owner"),
        };
        return new AppException(code, detail)
        {
            Extensions = mode == AccessMode.Deleted ? null : new JsonObject
            {
                ["recovery"] = new JsonObject
                {
                    ["action"] = action,
                    ["blockedCategory"] = SaasWire.Category(category),
                    ["restrictionReasons"] = reasons,
                },
            },
        };
    }

    public static AppException PlanLimit(QuotaKey quota, long used, long limit) =>
        new(ErrorCode.PlanLimitReached, $"The plan limit for {SaasWire.Quota(quota)} is reached ({used} of {limit}).")
        {
            Extensions = new JsonObject
            {
                ["quota"] = new JsonObject
                {
                    ["quota"] = SaasWire.Quota(quota),
                    ["used"] = used,
                    ["limit"] = limit,
                    ["upgradeAction"] = "upgrade-plan",
                },
            },
        };
}

/// <summary>
/// Atomic quota reservation inside the command transaction (ENT-003, DEC-038): lock the tenant's usage counter,
/// compare with the limit from the current entitlement snapshot read in the same transaction, then count.
/// </summary>
public interface IQuotaGate
{
    /// <summary>Counts <paramref name="amount"/> against the quota or throws 409 PLAN_LIMIT_REACHED.</summary>
    Task ReserveAsync(Persistence.ITenantSession session, QuotaKey quota, long amount = 1, CancellationToken ct = default);

    /// <summary>Returns usage when a counted resource is retired or deactivated (never below zero).</summary>
    Task ReleaseAsync(Persistence.ITenantSession session, QuotaKey quota, long amount = 1, CancellationToken ct = default);
}
