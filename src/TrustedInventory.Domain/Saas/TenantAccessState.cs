namespace TrustedInventory.Domain.Saas;

/// <summary>
/// A tenant's stored lifecycle, subscription and entitlement state, as returned by cp.tenant_access().
/// Access mode and restriction reasons are computed from it at a given instant (DEC-033); nothing waits for a
/// worker to move a boundary (section 26).
/// </summary>
public sealed record TenantAccessState(
    Guid TenantId,
    TenantLifecycle Lifecycle,
    DateTimeOffset? SecuritySuspendedAt,
    SubscriptionState? Subscription,
    DateTimeOffset? TrialEnd,
    DateTimeOffset? PeriodEnd,
    DateTimeOffset? GraceEnd,
    bool CancelAtPeriodEnd,
    long? SubscriptionRevision,
    int? EntitlementVersion,
    IReadOnlySet<string> Features,
    IReadOnlyDictionary<QuotaKey, long> Limits,
    DateTimeOffset? EntitlementEffectiveTo)
{
    /// <summary>Every active reason, highest precedence first (LFC-001).</summary>
    public IReadOnlyList<RestrictionReason> Reasons(DateTimeOffset now)
    {
        var reasons = new List<RestrictionReason>();
        switch (Lifecycle)
        {
            case TenantLifecycle.Deleted:
                reasons.Add(RestrictionReason.Deleted);
                break;
            case TenantLifecycle.DeletionPending:
                reasons.Add(RestrictionReason.DeletionPending);
                break;
            case TenantLifecycle.Archived:
                reasons.Add(RestrictionReason.Archived);
                break;
        }
        if (SecuritySuspendedAt is not null)
            reasons.Add(RestrictionReason.SecuritySuspended);
        if (BillingReason(now) is { } billing)
            reasons.Add(billing);
        if (Lifecycle == TenantLifecycle.Provisioning)
            reasons.Add(RestrictionReason.Provisioning);
        return reasons;
    }

    public AccessMode Mode(DateTimeOffset now)
    {
        if (Lifecycle == TenantLifecycle.Deleted)
            return AccessMode.Deleted;
        if (Lifecycle is TenantLifecycle.Archived or TenantLifecycle.DeletionPending)
            return AccessMode.Closed;
        if (SecuritySuspendedAt is not null)
            return AccessMode.SecuritySuspended;
        if (Lifecycle == TenantLifecycle.Provisioning)
            return AccessMode.Provisioning;
        return BillingReason(now) is null ? AccessMode.Normal : AccessMode.BillingRestricted;
    }

    /// <summary>
    /// Computed billing restriction (docs/saas-state-matrix.md). A Ready tenant with no subscription is treated
    /// as ended, failing closed: every tenant must have one (SAS-001).
    /// </summary>
    public RestrictionReason? BillingReason(DateTimeOffset now)
    {
        if (Lifecycle == TenantLifecycle.Provisioning)
            return null; // the trial starts at Ready (ONB-003)
        return Subscription switch
        {
            null => RestrictionReason.SubscriptionEnded,
            SubscriptionState.Cancelled => RestrictionReason.SubscriptionEnded,
            _ when CancelAtPeriodEnd && PeriodEnd is { } end && now >= end => RestrictionReason.SubscriptionEnded,
            SubscriptionState.Trialing when TrialEnd is { } trialEnd && now >= trialEnd => RestrictionReason.TrialEnded,
            SubscriptionState.PastDue when GraceEnd is { } graceEnd && now >= graceEnd => RestrictionReason.PaymentOverdue,
            _ => null,
        };
    }

    /// <summary>An expired entitlement snapshot grants nothing (fail closed); safety never consults this.</summary>
    public bool HasFeature(string feature, DateTimeOffset now) =>
        EntitlementVersion is not null && (EntitlementEffectiveTo is null || now < EntitlementEffectiveTo) && Features.Contains(feature);

    public long? Limit(QuotaKey quota, DateTimeOffset now) =>
        EntitlementVersion is not null && (EntitlementEffectiveTo is null || now < EntitlementEffectiveTo)
            ? (Limits.TryGetValue(quota, out var limit) ? limit : 0)
            : 0;
}
