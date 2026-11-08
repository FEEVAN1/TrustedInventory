using TrustedInventory.Domain.Common;
using TrustedInventory.Domain.Outbound;

namespace TrustedInventory.UnitTests;

/// <summary>Order aggregate status derived from line quantities (section 13, DEC-052).</summary>
public class OutboundOrderStatusTests
{
    private static LineProgress L(decimal requested, decimal allocated = 0, decimal picked = 0, decimal dispatched = 0) =>
        new(requested, allocated, picked, dispatched);

    public static TheoryData<LineProgress[], OutboundOrderStatus> Cases() => new()
    {
        { [L(10), L(5)], OutboundOrderStatus.Confirmed },
        { [L(10, 4), L(5)], OutboundOrderStatus.PartAllocated },
        { [L(10, 10), L(5, 5)], OutboundOrderStatus.Allocated },
        { [L(10, 10, 3), L(5, 5)], OutboundOrderStatus.PartPicked },
        { [L(10, 10, 10), L(5, 5, 5)], OutboundOrderStatus.Picked },
        { [L(10, 10, 10, 10), L(5, 5, 5)], OutboundOrderStatus.PartDispatched },  // partial dispatch leaves the order open
        { [L(10, 10, 10, 10), L(5, 5, 5, 5)], OutboundOrderStatus.Dispatched },
        { [L(10, 10, 10, 2), L(5)], OutboundOrderStatus.PartDispatched },          // any dispatch outranks picking progress
    };

    [Theory]
    [MemberData(nameof(Cases))]
    [Trait("Requirement", "OUT-001")]
    [Trait("Requirement", "STATE-13")]
    public void Status_follows_the_most_advanced_line_progress(LineProgress[] lines, OutboundOrderStatus expected) =>
        Assert.Equal(expected, OutboundOrder.Derive(lines));

    [Fact]
    [Trait("Requirement", "OUT-001")]
    [Trait("Requirement", "STATE-13")]
    public void Draft_and_cancelled_are_decisions_that_line_changes_never_override()
    {
        var now = DateTimeOffset.UnixEpoch;
        var order = OutboundOrder.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "SO-1", new DateOnly(2027, 1, 1), "u", now);
        order.Recalculate([L(10, 10)], now);
        Assert.Equal(OutboundOrderStatus.Draft, order.Status);

        order.Confirm("u", now);
        order.Recalculate([L(10, 4)], now);
        Assert.Equal(OutboundOrderStatus.PartAllocated, order.Status);

        Assert.Throws<DomainException>(() => order.Cancel("no", [L(10, 4, 1)], now)); // picked stock must be unpicked first
        order.Cancel("Customer withdrew", [L(10, 0)], now);
        order.Recalculate([L(10, 10)], now);
        Assert.Equal(OutboundOrderStatus.Cancelled, order.Status);
        Assert.Throws<DomainException>(() => order.Confirm("u", now));
        Assert.Throws<DomainException>(() => order.EditDraft("SO-2", null, now));
    }
}
