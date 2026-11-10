using TrustedInventory.Domain.Common;
using TrustedInventory.Domain.Operations;

namespace TrustedInventory.UnitTests;

/// <summary>Transfer lifecycle and line arithmetic (EXC-001, STK-003, DEC-058).</summary>
public class TransferRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();

    [Fact]
    [Trait("Requirement", "EXC-001")]
    [Trait("Requirement", "STK-003")]
    public void Status_follows_dispatch_receipt_and_disposition_and_completes_only_when_nothing_is_left_in_transit()
    {
        var transfer = Transfer.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), "TR-1", "planner", Now);
        var line = TransferLine.Create(Tenant, transfer.Id, 1, Guid.NewGuid(), null, 20, "planner", Now);
        Assert.Throws<DomainException>(() => transfer.Recalculate([line], "clerk", Now)); // not approved yet
        transfer.Approve("manager", Now);

        line.Dispatch(20, Now);
        transfer.Recalculate([line], "clerk", Now);
        Assert.Equal((TransferStatus.InTransit, 20m), (transfer.Status, line.InTransitQty));
        Assert.Throws<DomainException>(() => line.Dispatch(1, Now));

        line.Receive(18, Now);
        transfer.Recalculate([line], "receiver", Now);
        Assert.Equal((TransferStatus.PartReceived, 2m, (DateTimeOffset?)null), (transfer.Status, line.InTransitQty, transfer.ReceivedAt));
        Assert.Throws<DomainException>(() => line.Receive(3, Now));
        Assert.Throws<DomainException>(() => transfer.Cancel("manager", [line], Now));

        line.Dispose(2, Now);
        transfer.Recalculate([line], "receiver", Now);
        Assert.Equal((TransferStatus.Received, "receiver"), (transfer.Status, transfer.LastModifiedBy));
        Assert.NotNull(transfer.ReceivedAt);
    }

    [Fact]
    [Trait("Requirement", "EXC-001")]
    public void A_transfer_crosses_warehouses_and_is_cancelled_only_before_dispatch()
    {
        var site = Guid.NewGuid();
        Assert.Throws<DomainException>(() => Transfer.Create(Tenant, site, site, "TR-2", "planner", Now));
        var transfer = Transfer.Create(Tenant, site, Guid.NewGuid(), "TR-2", "planner", Now);
        var line = TransferLine.Create(Tenant, transfer.Id, 1, Guid.NewGuid(), null, 5, "planner", Now);
        transfer.Approve("manager", Now);
        transfer.Cancel("manager", [line], Now);
        Assert.Equal(TransferStatus.Cancelled, transfer.Status);
        Assert.Throws<DomainException>(() => transfer.Approve("manager", Now));
    }
}
