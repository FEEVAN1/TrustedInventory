using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Locations;

namespace TrustedInventory.UnitTests;

/// <summary>Putaway recommendation (INB-005, DEC-050): hard constraints, then ranking.</summary>
public class PutawayPlannerTests
{
    private static BinCandidate Bin(string code, int priority = 5, BinKind kind = BinKind.Storage, bool active = true, bool compatible = true,
        decimal? capacity = null, decimal? qty = 1, decimal? load = 0, bool same = false) =>
        new(Guid.NewGuid(), code, kind, active, priority, compatible, capacity, qty, load, same);

    [Fact]
    [Trait("Requirement", "INB-005")]
    [Trait("Requirement", "STORE-COMPAT")]
    public void Hard_constraints_exclude_inactive_incompatible_wrong_kind_and_full_bins()
    {
        Assert.True(PutawayPlanner.IsSuitable(Bin("OK"), QualityStatus.Available));
        Assert.False(PutawayPlanner.IsSuitable(Bin("OFF", active: false), QualityStatus.Available));
        Assert.False(PutawayPlanner.IsSuitable(Bin("WARM", compatible: false), QualityStatus.Available));
        Assert.False(PutawayPlanner.IsSuitable(Bin("STG", kind: BinKind.Staging), QualityStatus.Available));
        Assert.False(PutawayPlanner.IsSuitable(Bin("TRN", kind: BinKind.Transit), QualityStatus.Quarantine));
        // Released stock goes to storage only; held stock may also go to a quarantine bin and keeps its quality.
        Assert.False(PutawayPlanner.IsSuitable(Bin("Q", kind: BinKind.Quarantine), QualityStatus.Available));
        Assert.True(PutawayPlanner.IsSuitable(Bin("Q", kind: BinKind.Quarantine), QualityStatus.Quarantine));
        Assert.True(PutawayPlanner.IsSuitable(Bin("Q", kind: BinKind.Quarantine), QualityStatus.Hold));
    }

    [Theory]
    [Trait("Requirement", "INB-005")]
    [InlineData(10.0, 4.0, 6.0, true)]   // exactly full is fine
    [InlineData(10.0, 4.0, 6.5, false)]  // over capacity
    [InlineData(10.0, null, 1.0, false)] // the moved quantity cannot be expressed in the capacity unit: fail closed
    [InlineData(10.0, 1.0, null, false)] // the bin's current load cannot be expressed: fail closed
    public void Capacity_is_a_hard_constraint_that_fails_closed(double capacity, double? qty, double? load, bool suitable) =>
        Assert.Equal(suitable, PutawayPlanner.IsSuitable(Bin("CAP", capacity: (decimal)capacity, qty: (decimal?)qty, load: (decimal?)load), QualityStatus.Available));

    [Fact]
    [Trait("Requirement", "INB-005")]
    public void Ranking_prefers_same_product_and_lot_then_pick_priority_then_code()
    {
        var ranked = PutawayPlanner.Rank(
        [
            Bin("C-02", priority: 1), Bin("C-01", priority: 1), Bin("A-09", priority: 9, same: true), Bin("B-00", priority: 0),
            Bin("Z-FULL", priority: 0, capacity: 1, qty: 2), Bin("Z-STG", priority: 0, kind: BinKind.Staging),
        ], QualityStatus.Available);
        Assert.Equal(["A-09", "B-00", "C-01", "C-02"], ranked.Select(r => r.Code));
        Assert.Equal([1, 2, 3, 4], ranked.Select(r => r.Rank));
        Assert.Equal("Already holds this product and lot", ranked[0].Reason);
        Assert.Equal("Pick priority 0", ranked[1].Reason);
        Assert.Empty(PutawayPlanner.Rank([Bin("X", compatible: false)], QualityStatus.Available)); // nothing suitable: an exception task
    }

    [Fact]
    [Trait("Requirement", "INB-005")]
    public void A_task_without_a_recommendation_is_an_exception_and_completes_with_its_resolution()
    {
        var now = DateTimeOffset.UnixEpoch;
        var exception = WarehouseTask.ForReceiptLine(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), 5, null, "u", now);
        Assert.Equal(TaskType.Exception, exception.Type);
        exception.Confirm(2, "u", "Moved to new hazmat cage", now);
        Assert.Equal(TrustedInventory.Domain.Inbound.TaskStatus.InProgress, exception.Status);
        exception.Confirm(3, "u", "Moved to new hazmat cage", now);
        Assert.Equal(TrustedInventory.Domain.Inbound.TaskStatus.Completed, exception.Status);
        Assert.Equal("Moved to new hazmat cage", exception.Resolution);
        Assert.Throws<TrustedInventory.Domain.Common.DomainException>(() => exception.Confirm(1, "u", null, now));
    }
}
