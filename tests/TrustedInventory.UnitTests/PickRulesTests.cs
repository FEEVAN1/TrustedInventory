using TrustedInventory.Domain.Common;
using TrustedInventory.Domain.Outbound;
using TaskStatus = TrustedInventory.Domain.Inbound.TaskStatus;

namespace TrustedInventory.UnitTests;

/// <summary>Pick tasks and reservation segments (OUT-003, OUT-004, STK-002, STK-008, DEC-054).</summary>
public class PickRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();

    private static Pick NewPick(decimal qty) =>
        Pick.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, qty, "picker", Now);

    private static Reservation Active(decimal qty) =>
        Reservation.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), qty, Now.AddHours(24), "planner", Now);

    [Fact]
    [Trait("Requirement", "OUT-004")]
    public void A_pick_progresses_open_in_progress_completed_and_never_overpicks()
    {
        var pick = NewPick(6);
        Assert.Throws<DomainException>(() => pick.Confirm(7, Now));
        pick.Confirm(4, Now);
        Assert.Equal((TaskStatus.InProgress, 2m), (pick.Status, pick.RemainingQty));
        pick.Confirm(2, Now);
        Assert.Equal((TaskStatus.Completed, 0m), (pick.Status, pick.RemainingQty));
        Assert.Throws<DomainException>(() => pick.Confirm(1, Now));
    }

    [Fact]
    [Trait("Requirement", "OUT-006")]
    public void Unpick_reopens_the_task_and_closing_the_remainder_keeps_what_was_picked()
    {
        var pick = NewPick(6);
        pick.Confirm(6, Now);
        var bin = Guid.NewGuid();
        Assert.Throws<DomainException>(() => pick.Unpick(7, bin, Now));
        pick.Unpick(2, bin, Now);
        Assert.Equal((TaskStatus.InProgress, 4m, bin), (pick.Status, pick.PickedQty, pick.SourceBinId));

        pick.CloseRemainder(Now);
        Assert.Equal((TaskStatus.Completed, 4m, 4m), (pick.Status, pick.TaskQty, pick.PickedQty));

        var untouched = NewPick(3);
        untouched.CloseRemainder(Now);
        Assert.Equal(TaskStatus.Cancelled, untouched.Status);
    }

    [Fact]
    [Trait("Requirement", "STK-002")]
    [Trait("Requirement", "STK-008")]
    public void Segments_split_fold_and_return_without_ever_becoming_empty()
    {
        var own = Active(6);
        var staging = Guid.NewGuid();
        Assert.Throws<DomainException>(() => own.Shrink(6, Now)); // a full pick moves the segment instead of emptying it
        own.Shrink(4, Now);
        var child = Reservation.CreatePicked(Tenant, own.OrderLineId, own.ProductId, staging, 4, own.Id, "picker", Now);
        Assert.Null(child.ExpiresAt);                               // picked reservations never expire

        own.MoveToStaging(staging, child.Qty, Now);
        child.Fold(Now);
        Assert.Equal((ReservationState.Picked, 6m, staging, (DateTimeOffset?)null), (own.State, own.Qty, own.BucketId, own.ExpiresAt));
        Assert.Equal(ReservationState.Released, child.State);
        Assert.Throws<DomainException>(() => own.Fold(Now));         // only a child folds

        var back = Guid.NewGuid();
        own.ReturnToActive(back, 2, Now.AddHours(24), Now);
        Assert.Equal((ReservationState.Active, 2m, back), (own.State, own.Qty, own.BucketId));
        Assert.Throws<DomainException>(() => own.ReturnToActive(back, 1, Now.AddHours(24), Now));
    }

    [Fact]
    [Trait("Requirement", "OUT-006")]
    public void Releasing_a_part_picked_segment_takes_over_its_picked_child()
    {
        var own = Active(6);
        own.Shrink(4, Now);
        var staging = Guid.NewGuid();
        var child = Reservation.CreatePicked(Tenant, own.OrderLineId, own.ProductId, staging, 4, own.Id, "picker", Now);

        own.ReleaseKeepingPicked(child, Now);
        Assert.Equal((ReservationState.Picked, 4m, staging), (own.State, own.Qty, own.BucketId));
        Assert.Equal(ReservationState.Released, child.State);
        Assert.Throws<DomainException>(() => Active(1).ReleaseKeepingPicked(child, Now));
    }

    [Fact]
    [Trait("Requirement", "STK-008")]
    public void Only_due_active_segments_expire_and_only_picked_segments_are_consumed()
    {
        var active = Active(5);
        Assert.Throws<DomainException>(() => active.Expire(Now));               // not due yet
        Assert.Throws<DomainException>(() => active.Consume(Now));              // unpicked stock never ships
        active.Expire(Now.AddHours(24));
        Assert.Equal((ReservationState.Expired, (DateTimeOffset?)null), (active.State, active.ExpiresAt));

        var picked = Active(3);
        picked.MoveToStaging(Guid.NewGuid(), 0, Now);
        Assert.Throws<DomainException>(() => picked.Expire(Now.AddDays(30)));   // picked never expires
        picked.Consume(Now);
        Assert.Equal(ReservationState.Consumed, picked.State);
        Assert.Throws<DomainException>(() => picked.Consume(Now));              // consumed once
    }

    [Fact]
    [Trait("Requirement", "OUT-005")]
    [Trait("Requirement", "STK-001")]
    public void Lines_ship_only_picked_quantity_and_a_lot_stops_shipping_on_its_expiry_day()
    {
        var line = OutboundOrderLine.Create(Tenant, Guid.NewGuid(), 1, Guid.NewGuid(), 10, 0, "planner", Now);
        line.Allocate(10, Now);
        line.Pick(6, Now);
        Assert.Throws<DomainException>(() => line.Dispatch(7, Now));
        line.Dispatch(6, Now);
        Assert.Throws<DomainException>(() => line.Unpick(1, Now));               // dispatched stock cannot be unpicked

        var today = new DateOnly(2026, 11, 9);
        Assert.True(Domain.Inbound.ReceivingRules.IsShippable(today.AddDays(1), today));
        Assert.False(Domain.Inbound.ReceivingRules.IsShippable(today, today));
        Assert.True(Domain.Inbound.ReceivingRules.IsShippable(null, today));
    }
}
