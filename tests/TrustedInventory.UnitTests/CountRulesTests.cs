using TrustedInventory.Domain.Common;
using TrustedInventory.Domain.Operations;

namespace TrustedInventory.UnitTests;

/// <summary>Count lifecycle and observations (EXC-003, AT-EXC-04, DEC-059).</summary>
public class CountRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();

    [Fact]
    [Trait("Requirement", "EXC-003")]
    [Trait("Requirement", "AT-EXC-04")]
    public void A_count_moves_forward_and_a_rejection_opens_a_new_revision()
    {
        var count = StockCount.Create(Tenant, Guid.NewGuid(), "Cycle count", "planner", Now);
        Assert.Throws<DomainException>(() => count.Submit("counter", Now));
        count.Start(42, "counter", Now);
        Assert.Equal((CountStatus.Counting, 42L), (count.Status, count.SnapshotSequence!.Value));
        count.Submit("counter", Now);
        Assert.Throws<DomainException>(() => count.Post(true, "manager", Now));
        count.Reject("manager", Now);
        Assert.Equal((CountStatus.Counting, 2, (Guid?)null), (count.Status, count.Revision, count.ApprovalId));
        count.Submit("counter", Now);
        count.Approve(Guid.NewGuid(), "manager", Now);
        count.Post(anyVariance: false, "manager", Now);
        Assert.Equal(CountStatus.Closed, count.Status);
        Assert.Throws<DomainException>(() => count.Cancel("manager", Now));
    }

    [Fact]
    [Trait("Requirement", "EXC-003")]
    public void A_line_is_observed_once_per_revision_and_variance_is_observed_minus_expected()
    {
        var line = StockCountLine.Create(Tenant, Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), null, null, 10, "planner", Now);
        Assert.Null(line.Variance);
        Assert.Throws<DomainException>(() => line.Observe(-1, "counter", Now));
        line.Observe(7, "counter", Now);
        Assert.Equal(-3m, line.Variance);
        Assert.Throws<DomainException>(() => line.Observe(8, "counter", Now));
    }
}
