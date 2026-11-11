namespace TrustedInventory.Domain.Saas;

/// <summary>The subscription facts a verified billing event is applied to (DEC-064).</summary>
public sealed record SubscriptionFacts(
    SubscriptionState State,
    string? ProviderSubscriptionRef,
    long? ProviderRevision,
    DateTimeOffset? PeriodStart,
    DateTimeOffset? PeriodEnd,
    DateTimeOffset? GraceEnd);

public enum ActivationOutcome
{
    /// <summary>Trialing or Cancelled with no provider subscription yet: becomes Active.</summary>
    Activate,

    /// <summary>The same provider subscription already activated it: a duplicate, nothing changes.</summary>
    AlreadyActive,

    /// <summary>Linked to another provider subscription, or in a state a checkout cannot start from: operator review.</summary>
    Conflict,
}

/// <summary>
/// How verified provider events change a subscription (BIL-005, BIL-007, DEC-064). Pure rules: the processor loads the
/// facts under lock, asks these, and writes the answer. A null result means "no change".
/// </summary>
public static class BillingEventRules
{
    /// <summary>One grace period from the unpaid period's due date (BIL-007; 7 days is the D10 proposal).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromDays(7);

    /// <summary>Monotonic provider ordering: an event applies only if it is newer than what we hold.</summary>
    public static bool IsNewer(long incoming, long? stored) => stored is null || incoming > stored;

    /// <summary>A paid invoice never goes back; otherwise the newer revision wins.</summary>
    public static bool InvoiceChanges(InvoiceStatus? current, long? currentRevision, long incomingRevision) =>
        current is null || (current != InvoiceStatus.Paid && IsNewer(incomingRevision, currentRevision));

    public static ActivationOutcome Activation(SubscriptionFacts s, string providerSubscriptionRef)
    {
        if (s.ProviderSubscriptionRef == providerSubscriptionRef)
            return ActivationOutcome.AlreadyActive;
        return s.State switch
        {
            // An ended subscription starts again under the new provider subscription (DEC-063: checkout from Cancelled).
            SubscriptionState.Cancelled => ActivationOutcome.Activate,
            SubscriptionState.Trialing when s.ProviderSubscriptionRef is null => ActivationOutcome.Activate,
            _ => ActivationOutcome.Conflict,
        };
    }

    /// <summary>
    /// A renewal invoice was paid. The period advances; PastDue clears only when no other dues remain open (BIL-007).
    /// </summary>
    public static SubscriptionFacts? RenewalPaid(SubscriptionFacts s, long revision, DateTimeOffset periodStart, DateTimeOffset periodEnd,
        bool otherDuesOpen)
    {
        if (!IsNewer(revision, s.ProviderRevision) || s.State is not (SubscriptionState.Active or SubscriptionState.PastDue))
            return null;
        if (periodEnd <= periodStart)
            throw new ArgumentException("A billing period must end after it starts.", nameof(periodEnd));
        var next = s with { ProviderRevision = revision };
        if (s.PeriodEnd is null || periodEnd > s.PeriodEnd)
            next = next with { PeriodStart = periodStart, PeriodEnd = periodEnd };
        if (s.State == SubscriptionState.PastDue && !otherDuesOpen)
            next = next with { State = SubscriptionState.Active, GraceEnd = null };
        return next;
    }

    /// <summary>
    /// A renewal payment failed. Active becomes PastDue with one grace period from the due date; a subscription that is
    /// already PastDue keeps its grace end however often the provider retries (BIL-007).
    /// </summary>
    public static SubscriptionFacts? RenewalFailed(SubscriptionFacts s, long revision, DateTimeOffset dueAt)
    {
        if (!IsNewer(revision, s.ProviderRevision))
            return null;
        return s.State switch
        {
            SubscriptionState.Active => s with { State = SubscriptionState.PastDue, GraceEnd = dueAt + Grace, ProviderRevision = revision },
            SubscriptionState.PastDue => s with { ProviderRevision = revision },
            _ => null,
        };
    }
}

/// <summary>One reason a plan change cannot proceed (contract schema Blocker).</summary>
public sealed record ChangeBlocker(string Code, QuotaKey? Quota, long? Used, long? Limit, string Detail);

/// <summary>Plan change rules (ENT-005, ENT-006, DEC-065).</summary>
public static class PlanChangeRules
{
    public const string MedicalFeature = "medical_storage";

    /// <summary>
    /// An upgrade only adds: no feature removed, no limit lowered, something raised or added. It waits for the verified
    /// payment of its first period (ENT-005); anything else applies at the boundary once it fits.
    /// </summary>
    public static bool IsUpgrade(IReadOnlySet<string> currentFeatures, IReadOnlyDictionary<QuotaKey, long> currentLimits,
        IReadOnlySet<string> targetFeatures, IReadOnlyDictionary<QuotaKey, long> targetLimits)
    {
        if (!currentFeatures.IsSubsetOf(targetFeatures))
            return false;
        var raised = targetFeatures.Count > currentFeatures.Count;
        foreach (var quota in Enum.GetValues<QuotaKey>())
        {
            var current = currentLimits.GetValueOrDefault(quota);
            var target = targetLimits.GetValueOrDefault(quota);
            if (target < current)
                return false;
            raised |= target > current;
        }
        return raised;
    }

    public static bool RemovesMedical(IReadOnlySet<string> currentFeatures, IReadOnlySet<string> targetFeatures) =>
        currentFeatures.Contains(MedicalFeature) && !targetFeatures.Contains(MedicalFeature);

    /// <summary>Every quota whose current usage would exceed the target limit (a missing limit is 0). Never trims usage.</summary>
    public static IReadOnlyList<ChangeBlocker> QuotaBlockers(IReadOnlyDictionary<QuotaKey, long> usage, IReadOnlyDictionary<QuotaKey, long> targetLimits)
    {
        var blockers = new List<ChangeBlocker>();
        foreach (var quota in Enum.GetValues<QuotaKey>())
        {
            var used = usage.GetValueOrDefault(quota);
            var limit = targetLimits.GetValueOrDefault(quota);
            if (used > limit)
                blockers.Add(new ChangeBlocker("QUOTA_EXCEEDED", quota, used, limit,
                    $"{used} in use but the new plan allows {limit}. Reduce usage before the change takes effect; nothing is removed automatically."));
        }
        return blockers;
    }

    /// <summary>The provider ended the subscription (cancelled at period end): Cancelled, if the event is newer.</summary>
    public static SubscriptionFacts? Ended(SubscriptionFacts s, long revision) =>
        BillingEventRules.IsNewer(revision, s.ProviderRevision) && s.State is SubscriptionState.Active or SubscriptionState.PastDue
            ? s with { State = SubscriptionState.Cancelled, ProviderRevision = revision }
            : null;
}
