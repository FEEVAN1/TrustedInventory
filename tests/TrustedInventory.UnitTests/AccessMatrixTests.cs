using TrustedInventory.Domain.Saas;

namespace TrustedInventory.UnitTests;

/// <summary>The state matrix (docs/saas-state-matrix.md) cell by cell, and the access-mode computation.</summary>
public class AccessMatrixTests
{
    // Rows: category; columns: Normal, Provisioning, BillingRestricted, SecuritySuspended, Closed, Deleted.
    // Values: Y allowed, N rejected, O owner only. Copied from the document's table.
    private static readonly Dictionary<AccessCategory, string> Table = new()
    {
        [AccessCategory.Identity] = "YYYYYN",
        [AccessCategory.SetupStatus] = "YOYNON",
        [AccessCategory.Read] = "YNYNNN",
        [AccessCategory.Export] = "YNYNON",
        [AccessCategory.RoutineWrite] = "YNNNNN",
        [AccessCategory.Safety] = "YNYNYN",
        [AccessCategory.TeamAdmin] = "YNNNNN",
        [AccessCategory.ProtectiveAdmin] = "YNYNYN",
        [AccessCategory.Billing] = "YNYNYN",
        [AccessCategory.Ownership] = "YNYNNN",
        [AccessCategory.TelemetryIngest] = "YNYYYN",
    };

    private static readonly AccessMode[] Columns =
        [AccessMode.Normal, AccessMode.Provisioning, AccessMode.BillingRestricted, AccessMode.SecuritySuspended, AccessMode.Closed, AccessMode.Deleted];

    public static TheoryData<AccessCategory, AccessMode, char> Cells()
    {
        var data = new TheoryData<AccessCategory, AccessMode, char>();
        foreach (var (category, row) in Table)
            for (var i = 0; i < Columns.Length; i++)
                data.Add(category, Columns[i], row[i]);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    [Trait("Requirement", "LFC-001")]
    [Trait("Requirement", "SAS-005")]
    public void Matrix_cell_matches_the_document(AccessCategory category, AccessMode mode, char expected)
    {
        Assert.Equal(expected is 'Y' or 'O', AccessMatrix.Allows(mode, category, isOwner: true));
        Assert.Equal(expected == 'Y', AccessMatrix.Allows(mode, category, isOwner: false));
    }

    [Fact]
    [Trait("Requirement", "LFC-001")]
    public void Every_category_has_a_row()
    {
        Assert.Equal(Enum.GetValues<AccessCategory>().Order(), Table.Keys.Order());
    }

    [Fact]
    [Trait("Requirement", "ENT-006")]
    [Trait("Requirement", "SUI-003")]
    public void Safety_never_uses_entitlements()
    {
        Assert.False(AccessMatrix.UsesEntitlements(AccessCategory.Safety));
        Assert.All(Enum.GetValues<AccessCategory>().Where(c => c != AccessCategory.Safety),
            c => Assert.True(AccessMatrix.UsesEntitlements(c)));
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static TenantAccessState State(TenantLifecycle lifecycle = TenantLifecycle.Ready, bool suspended = false,
        SubscriptionState? sub = SubscriptionState.Active, DateTimeOffset? trialEnd = null, DateTimeOffset? periodEnd = null,
        DateTimeOffset? graceEnd = null, bool cancelAtPeriodEnd = false) =>
        new(Guid.NewGuid(), lifecycle, suspended ? Now.AddDays(-1) : null, sub, trialEnd, periodEnd ?? Now.AddDays(30), graceEnd,
            cancelAtPeriodEnd, 1, 1, new HashSet<string> { FeatureKeys.CoreInventory }, new Dictionary<QuotaKey, long>(), null);

    [Fact]
    [Trait("Requirement", "BIL-008")]
    [Trait("Requirement", "LFC-001")]
    public void Complimentary_access_replaces_billing_and_entitlements_until_its_expiry_but_never_lifts_security_or_closure()
    {
        var until = Now.AddDays(10);
        var granted = new HashSet<string> { FeatureKeys.CoreInventory, FeatureKeys.AdvancedReporting };
        var limits = new Dictionary<QuotaKey, long> { [QuotaKey.ActiveWarehouses] = 7 };
        var ended = State(sub: SubscriptionState.Cancelled) with { ComplimentaryUntil = until, ComplimentaryFeatures = granted, ComplimentaryLimits = limits };

        Assert.Equal(AccessMode.Normal, ended.Mode(Now));
        Assert.Empty(ended.Reasons(Now));
        Assert.True(ended.HasFeature(FeatureKeys.AdvancedReporting, Now));
        Assert.Equal(7, ended.Limit(QuotaKey.ActiveWarehouses, Now));
        Assert.Equal(0, ended.Limit(QuotaKey.ActiveSkus, Now));

        // The expiry is exact, computed with the clock; nothing waits for a job.
        Assert.Equal(AccessMode.BillingRestricted, ended.Mode(until));
        Assert.Equal([RestrictionReason.SubscriptionEnded], ended.Reasons(until));
        Assert.False(ended.HasFeature(FeatureKeys.AdvancedReporting, until));

        Assert.Equal(AccessMode.SecuritySuspended, (ended with { SecuritySuspendedAt = Now.AddDays(-1) }).Mode(Now));
        Assert.Equal(AccessMode.Closed, (ended with { Lifecycle = TenantLifecycle.Archived }).Mode(Now));
    }

    [Fact]
    [Trait("Requirement", "LFC-001")]
    [Trait("Requirement", "AT-BIL-04")]
    public void Precedence_is_deleted_closed_security_billing_provisioning()
    {
        Assert.Equal(AccessMode.Deleted, State(TenantLifecycle.Deleted, suspended: true, sub: null).Mode(Now));
        Assert.Equal(AccessMode.Closed, State(TenantLifecycle.Archived, suspended: true, sub: null).Mode(Now));
        Assert.Equal(AccessMode.SecuritySuspended, State(suspended: true, sub: SubscriptionState.Cancelled).Mode(Now));
        // Paying (Active) never clears a security suspension.
        Assert.Equal(AccessMode.SecuritySuspended, State(suspended: true).Mode(Now));
        Assert.Equal(
            [RestrictionReason.SecuritySuspended, RestrictionReason.SubscriptionEnded],
            State(suspended: true, sub: SubscriptionState.Cancelled).Reasons(Now));
    }

    [Fact]
    [Trait("Requirement", "AT-BIL-03")]
    [Trait("Requirement", "BIL-007")]
    public void Billing_boundaries_apply_at_the_exact_instant()
    {
        Assert.Equal(AccessMode.Normal, State(sub: SubscriptionState.Trialing, trialEnd: Now.AddTicks(1)).Mode(Now));
        Assert.Equal(AccessMode.BillingRestricted, State(sub: SubscriptionState.Trialing, trialEnd: Now).Mode(Now));
        // PastDue inside grace is normal access; at grace_end it restricts.
        Assert.Equal(AccessMode.Normal, State(sub: SubscriptionState.PastDue, graceEnd: Now.AddMinutes(1)).Mode(Now));
        Assert.Equal(RestrictionReason.PaymentOverdue, State(sub: SubscriptionState.PastDue, graceEnd: Now).BillingReason(Now));
        // Cancelled at period end keeps access until the boundary (AT-LFC-01).
        Assert.Equal(AccessMode.Normal, State(cancelAtPeriodEnd: true, periodEnd: Now.AddSeconds(1)).Mode(Now));
        Assert.Equal(AccessMode.BillingRestricted, State(cancelAtPeriodEnd: true, periodEnd: Now).Mode(Now));
    }

    [Fact]
    [Trait("Requirement", "SAS-001")]
    public void A_ready_tenant_without_a_subscription_fails_closed_but_provisioning_does_not_start_billing()
    {
        Assert.Equal(AccessMode.BillingRestricted, State(sub: null).Mode(Now));
        Assert.Equal(AccessMode.Provisioning, State(TenantLifecycle.Provisioning, sub: null).Mode(Now));
        Assert.Equal([RestrictionReason.Provisioning], State(TenantLifecycle.Provisioning, sub: null).Reasons(Now));
    }

    [Fact]
    [Trait("Requirement", "ENT-002")]
    public void An_expired_entitlement_snapshot_grants_no_feature_and_no_quota()
    {
        var expired = State() with { EntitlementEffectiveTo = Now, Limits = new Dictionary<QuotaKey, long> { [QuotaKey.ActiveSeats] = 5 } };
        Assert.False(expired.HasFeature(FeatureKeys.CoreInventory, Now));
        Assert.Equal(0, expired.Limit(QuotaKey.ActiveSeats, Now));
        Assert.True(State().HasFeature(FeatureKeys.CoreInventory, Now));
        Assert.False(State().HasFeature(FeatureKeys.MedicalStorage, Now));
    }

    [Theory]
    [Trait("Requirement", "DEC-034")]
    [InlineData("routine-write", AccessCategory.RoutineWrite)]
    [InlineData("setup-status", AccessCategory.SetupStatus)]
    [InlineData("telemetry-ingest", AccessCategory.TelemetryIngest)]
    public void Wire_names_round_trip(string wire, AccessCategory category)
    {
        Assert.Equal(category, SaasWire.ParseCategory(wire));
        Assert.Equal(wire, SaasWire.Category(category));
    }
}
