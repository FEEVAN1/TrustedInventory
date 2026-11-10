using TrustedInventory.Domain.Common;
using TrustedInventory.Domain.Operations;

namespace TrustedInventory.UnitTests;

/// <summary>Customer return inspection arithmetic (EXC-005, DEC-060).</summary>
public class ReturnRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private static CustomerReturn Received(decimal qty, Guid? serial = null) => CustomerReturn.Receive(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), serial, qty, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "refused", "clerk", Now);

    [Fact]
    [Trait("Requirement", "EXC-005")]
    public void Inspections_split_the_return_and_it_is_inspected_only_when_nothing_is_left()
    {
        var ret = Received(5);
        ret.Inspect(ReturnDisposition.Release, 2, Now);
        Assert.Equal((ReturnStatus.Received, 3m, ReturnDisposition.Release), (ret.Status, ret.RemainingToInspect, ret.Disposition!.Value));
        Assert.Throws<DomainException>(() => ret.Inspect(ReturnDisposition.Disposal, 4, Now));
        ret.Inspect(ReturnDisposition.Disposal, 3, Now);
        Assert.Equal(ReturnStatus.Inspected, ret.Status);
        Assert.Throws<DomainException>(() => ret.Inspect(ReturnDisposition.Damaged, 1, Now));
    }

    [Fact]
    [Trait("Requirement", "EXC-005")]
    public void A_serial_tracked_return_is_one_unit()
    {
        Assert.Throws<DomainException>(() => Received(2, Guid.NewGuid()));
        Assert.Throws<DomainException>(() => Received(0));
        Assert.Equal(1m, Received(1, Guid.NewGuid()).ReturnedQty);
    }
}
