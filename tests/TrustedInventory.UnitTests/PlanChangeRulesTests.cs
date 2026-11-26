using TrustedInventory.Domain.Saas;

namespace TrustedInventory.UnitTests;

/// <summary>Which changes are upgrades, which usage blocks a downgrade, and how a provider end is applied (ENT-005, ENT-006, DEC-065).</summary>
public class PlanChangeRulesTests
{
    private static readonly HashSet<string> Core = ["core_inventory"];
    private static readonly HashSet<string> CoreMedical = ["core_inventory", "medical_storage"];

    private static Dictionary<QuotaKey, long> Limits(long each) => Enum.GetValues<QuotaKey>().ToDictionary(q => q, _ => each);

    [Fact]
    [Trait("Requirement", "ENT-005")]
    public void Only_a_change_that_adds_without_removing_is_an_upgrade()
    {
        Assert.True(PlanChangeRules.IsUpgrade(Core, Limits(10), Core, Limits(20)));
        Assert.True(PlanChangeRules.IsUpgrade(Core, Limits(10), CoreMedical, Limits(10)));
        Assert.False(PlanChangeRules.IsUpgrade(Core, Limits(10), Core, Limits(10))); // lateral, e.g. a new interval
        Assert.False(PlanChangeRules.IsUpgrade(CoreMedical, Limits(10), Core, Limits(100))); // drops a feature
        var mixed = Limits(20);
        mixed[QuotaKey.ActiveSeats] = 5;
        Assert.False(PlanChangeRules.IsUpgrade(Core, Limits(10), Core, mixed)); // lowers one limit
    }

    [Fact]
    [Trait("Requirement", "ENT-006")]
    public void Every_quota_above_the_target_limit_blocks_and_a_missing_limit_counts_as_zero()
    {
        var usage = new Dictionary<QuotaKey, long> { [QuotaKey.ActiveSeats] = 3, [QuotaKey.ActiveWarehouses] = 2 };
        var target = new Dictionary<QuotaKey, long> { [QuotaKey.ActiveSeats] = 3 };
        var blocker = Assert.Single(PlanChangeRules.QuotaBlockers(usage, target));
        Assert.Equal(("QUOTA_EXCEEDED", QuotaKey.ActiveWarehouses, 2L, 0L), (blocker.Code, blocker.Quota!.Value, blocker.Used!.Value, blocker.Limit!.Value));
        Assert.Empty(PlanChangeRules.QuotaBlockers(usage, Limits(3)));
        Assert.True(PlanChangeRules.RemovesMedical(CoreMedical, Core));
        Assert.False(PlanChangeRules.RemovesMedical(Core, CoreMedical));
    }

    [Fact]
    [Trait("Requirement", "BIL-008")]
    public void The_provider_ending_a_paid_subscription_cancels_it_once()
    {
        var active = new SubscriptionFacts(SubscriptionState.Active, "sim_sub_1", 4, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(30), null);
        var ended = PlanChangeRules.Ended(active, 5)!;
        Assert.Equal(SubscriptionState.Cancelled, ended.State);
        Assert.Null(PlanChangeRules.Ended(ended, 6));
        Assert.Null(PlanChangeRules.Ended(active, 4));
    }
}
