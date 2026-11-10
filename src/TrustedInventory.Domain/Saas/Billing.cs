using TrustedInventory.Domain.Common;
using System.Text.RegularExpressions;

namespace TrustedInventory.Domain.Saas;

// Values match docs/enums.md and the PostgreSQL enum labels; never reorder or rename.
public enum BillingInterval
{
    Month,
    Year,
}

public enum CheckoutStatus
{
    Open,
    Completed,
    Expired,
}

public enum InvoiceStatus
{
    Draft,
    Open,
    Paid,
    Void,
    Uncollectible,
}

public enum PaymentStatus
{
    Pending,
    Succeeded,
    Failed,
    Refunded,
}

public enum SubscriptionChangeState
{
    Pending,
    Scheduled,
    Blocked,
    Applied,
    Cancelled,
}

public enum PlanVersionStatus
{
    Draft,
    Published,
    Retired,
}

public enum ReconciliationTaskKind
{
    SubscriptionMismatch,
    UnpaidInvoice,
    EntitlementMismatch,
    UnknownBillingReference,
    Dispute,
    Refund,
    Chargeback,
}

public enum ReconciliationTaskStatus
{
    Open,
    Resolved,
}

public enum WebhookProcessingState
{
    Received,
    Processed,
    Ignored,
    Quarantined,
    Failed,
}

/// <summary>
/// Integer money (BIL-001): an ISO 4217 code, a non-negative amount in minor units and the currency's exponent.
/// Never floating point; there is no currency conversion in R1, so arithmetic requires one currency.
/// </summary>
public readonly partial record struct Money
{
    /// <summary>ISO 4217 code reserved for testing; the only currency the simulator accepts (DEC-040, DEC-062).</summary>
    public const string TestCurrency = "XTS";

    public string Currency { get; }
    public long MinorUnits { get; }
    public int Exponent { get; }

    private Money(string currency, long minorUnits, int exponent)
    {
        Currency = currency;
        MinorUnits = minorUnits;
        Exponent = exponent;
    }

    public static Money Of(string currency, long minorUnits, int exponent)
    {
        if (currency is null || !CurrencyPattern().IsMatch(currency))
            throw new DomainException("Currency must be a three-letter ISO 4217 code.");
        if (minorUnits < 0)
            throw new DomainException("Amounts cannot be negative.");
        if (exponent is < 0 or > 4)
            throw new DomainException("The currency exponent must be 0 to 4.");
        return new Money(currency, minorUnits, exponent);
    }

    public Money Plus(Money other) =>
        SameCurrency(other) ? new Money(Currency, checked(MinorUnits + other.MinorUnits), Exponent) : throw Mismatch();

    public Money Minus(Money other) =>
        SameCurrency(other) && other.MinorUnits <= MinorUnits ? new Money(Currency, MinorUnits - other.MinorUnits, Exponent)
        : throw (SameCurrency(other) ? new DomainException("The result would be negative.") : Mismatch());

    public bool SameCurrency(Money other) => Currency == other.Currency && Exponent == other.Exponent;

    private static DomainException Mismatch() => new("Amounts in different currencies cannot be combined (no conversion in R1).");

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();
}

/// <summary>Plan and price catalog rules (ENT-001, ENT-002, DEC-062).</summary>
public static partial class CatalogRules
{
    public static void ValidatePlan(string planCode, string name, IReadOnlyCollection<string> features,
        IReadOnlyDictionary<string, long?> limits, int trialDays)
    {
        // Matches both the contract pattern and cp.plan_version's CHECK (letter first, 2-40 characters).
        if (planCode is null || !PlanCodePattern().IsMatch(planCode))
            throw new DomainException("Plan codes are 2 to 40 lowercase letters, digits or hyphens, starting with a letter.");
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            throw new DomainException("Give the plan a name of 1 to 200 characters.");
        if (features.Count == 0 || features.Any(f => !FeatureKeys.All.Contains(f)) || features.Distinct().Count() != features.Count)
            throw new DomainException("Features must be distinct catalog feature keys.");
        var keys = Enum.GetValues<QuotaKey>().Select(SaasWire.Quota).ToHashSet(StringComparer.Ordinal);
        if (limits.Keys.Any(k => !keys.Contains(k)))
            throw new DomainException("Limits must use catalog quota keys.");
        if (keys.Any(k => !limits.TryGetValue(k, out var v) || v is null or < 0))
            throw new DomainException("Every quota key needs a non-negative limit.");
        if (trialDays is < 0 or > 90)
            throw new DomainException("Trials are 0 to 90 days.");
    }

    /// <summary>While the simulator is the provider, only the test currency can be priced (no live commerce, DEC-040).</summary>
    public static void ValidatePrice(Money amount, bool simulatorOnly)
    {
        if (simulatorOnly && amount.Currency != Money.TestCurrency)
            throw new DomainException($"Only the test currency {Money.TestCurrency} can be priced until the owner approves a provider and prices (D09).");
        if (amount.MinorUnits == 0)
            throw new DomainException("A purchasable price must be above zero.");
    }

    public static PlanVersionStatus Status(DateTimeOffset? publishedAt, IEnumerable<(DateTimeOffset From, DateTimeOffset? To)> prices,
        DateTimeOffset now) =>
        publishedAt is null ? PlanVersionStatus.Draft
        : prices.Any(p => p.To is null || p.To > now) ? PlanVersionStatus.Published
        : PlanVersionStatus.Retired;

    [GeneratedRegex("^[a-z][a-z0-9-]{1,39}$")]
    private static partial Regex PlanCodePattern();
}
