using TrustedInventory.Domain.Common;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Operations;

namespace TrustedInventory.UnitTests;

/// <summary>
/// Transition tables (STATE-13): every state of the approval, count and quality machines against every operation, so a transition
/// that is not in the table is refused, not merely untested.
/// </summary>
public class StateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();

    // ---- Approval: Pending -> Approved | Rejected | Invalidated; Approved -> Consumed | Expired | Invalidated; the rest are final.

    private static Approval ApprovalIn(ApprovalStatus status)
    {
        var a = Approval.Request(Tenant, ApprovalSubjectType.Adjustment, Guid.NewGuid(), 1, new byte[32], ApprovalAction.Post, "why", "requester", Now);
        switch (status)
        {
            case ApprovalStatus.Pending: break;
            case ApprovalStatus.Approved: Sign(a); break;
            case ApprovalStatus.Rejected: a.Reject("signer", "no", Now); break;
            case ApprovalStatus.Invalidated: a.Invalidate("subject changed", Now); break;
            case ApprovalStatus.Expired: Sign(a); a.Expire(a.ExpiresAt!.Value); break;
            case ApprovalStatus.Consumed: Sign(a); a.Consume("op", Now); break;
        }
        Assert.Equal(status, a.Status);
        return a;
    }

    private static void Sign(Approval a) => a.Sign("signer", SignatureMeaning.Approval, "ok", new byte[32], Now, Now);

    public static TheoryData<ApprovalStatus, string> ApprovalTable() => new()
    {
        // state, operations allowed from it (S sign, R reject, I invalidate, E expire after the lifetime, C consume before it)
        { ApprovalStatus.Pending, "SRI" },
        { ApprovalStatus.Approved, "IEC" },
        { ApprovalStatus.Rejected, "" },
        { ApprovalStatus.Invalidated, "" },
        { ApprovalStatus.Expired, "" },
        { ApprovalStatus.Consumed, "" },
    };

    [Theory]
    [MemberData(nameof(ApprovalTable))]
    [Trait("Requirement", "STATE-13")]
    [Trait("Requirement", "IAM-003")]
    public void Approval_transitions_follow_the_table(ApprovalStatus from, string allowed)
    {
        var operations = new Dictionary<char, Action<Approval>>
        {
            ['S'] = Sign,
            ['R'] = a => a.Reject("signer", "no", Now),
            ['I'] = a => a.Invalidate("changed", Now),
            ['E'] = a => a.Expire((a.ExpiresAt ?? Now) + TimeSpan.FromSeconds(1)),
            ['C'] = a => a.Consume("op", Now),
        };
        foreach (var (op, act) in operations)
        {
            var approval = ApprovalIn(from);
            if (allowed.Contains(op, StringComparison.Ordinal))
                act(approval);
            else
                Assert.Throws<DomainException>(() => act(approval));
        }
    }

    [Fact]
    [Trait("Requirement", "STATE-13")]
    [Trait("Requirement", "SOD-1")]
    public void An_approval_is_never_signed_or_rejected_by_its_requester_and_never_used_after_expiry()
    {
        Assert.Throws<DomainException>(() => ApprovalIn(ApprovalStatus.Pending).Sign("requester", SignatureMeaning.Approval, "mine", new byte[32], Now, Now));
        Assert.Throws<DomainException>(() => ApprovalIn(ApprovalStatus.Pending).Reject("requester", "mine", Now));
        var signed = ApprovalIn(ApprovalStatus.Approved);
        Assert.Throws<DomainException>(() => signed.Consume("late", signed.ExpiresAt!.Value));
        Assert.Throws<DomainException>(() => signed.Expire(signed.ExpiresAt!.Value - TimeSpan.FromSeconds(1)));
        Assert.Equal(ApprovalStatus.Expired, signed.EffectiveStatus(signed.ExpiresAt!.Value));
    }

    // ---- Count: Draft -> Counting -> Submitted -> Approved -> Posted | Closed; reject returns to Counting; cancel while open.

    private static StockCount CountIn(CountStatus status)
    {
        var c = StockCount.Create(Tenant, Guid.NewGuid(), "cycle count", "counter", Now);
        if (status == CountStatus.Cancelled) { c.Cancel("manager", Now); return c; }
        var path = new (CountStatus Reached, Action Step)[]
        {
            (CountStatus.Counting, () => c.Start(1, "counter", Now)),
            (CountStatus.Submitted, () => c.Submit("counter", Now)),
            (CountStatus.Approved, () => c.Approve(Guid.NewGuid(), "manager", Now)),
        };
        foreach (var (reached, step) in path)
        {
            if (c.Status == status) break;
            step();
            _ = reached;
        }
        if (status is CountStatus.Posted or CountStatus.Closed)
            c.Post(status == CountStatus.Posted, "manager", Now);
        Assert.Equal(status, c.Status);
        return c;
    }

    public static TheoryData<CountStatus, string> CountTable() => new()
    {
        // state, operations allowed (T start, U submit, A approve, R reject, P post, X cancel)
        { CountStatus.Draft, "TX" },
        { CountStatus.Counting, "UX" },
        { CountStatus.Submitted, "ARX" },
        { CountStatus.Approved, "RPX" },
        { CountStatus.Posted, "" },
        { CountStatus.Closed, "" },
        { CountStatus.Cancelled, "" },
    };

    [Theory]
    [MemberData(nameof(CountTable))]
    [Trait("Requirement", "STATE-13")]
    [Trait("Requirement", "EXC-003")]
    public void Count_transitions_follow_the_table(CountStatus from, string allowed)
    {
        var operations = new Dictionary<char, Action<StockCount>>
        {
            ['T'] = c => c.Start(1, "counter", Now),
            ['U'] = c => c.Submit("counter", Now),
            ['A'] = c => c.Approve(Guid.NewGuid(), "manager", Now),
            ['R'] = c => c.Reject("manager", Now),
            ['P'] = c => c.Post(true, "manager", Now),
            ['X'] = c => c.Cancel("manager", Now),
        };
        foreach (var (op, act) in operations)
        {
            var count = CountIn(from);
            if (allowed.Contains(op, StringComparison.Ordinal))
                act(count);
            else
                Assert.Throws<DomainException>(() => act(count));
        }
    }

    [Fact]
    [Trait("Requirement", "STATE-13")]
    public void A_rejected_count_returns_to_counting_as_a_new_revision_without_its_approval()
    {
        var count = CountIn(CountStatus.Approved);
        count.Reject("manager", Now);
        Assert.Equal((CountStatus.Counting, 2, (Guid?)null), (count.Status, count.Revision, count.ApprovalId));
    }

    // ---- Quality: which status may be held, released (and with which approval) or dispositioned.

    [Theory]
    [Trait("Requirement", "STATE-13")]
    [Trait("Requirement", "MED-001")]
    [Trait("Requirement", "EXC-007")]
    [InlineData(QualityStatus.Quarantine, true, ApprovalSubjectType.QualityRelease, true)]
    [InlineData(QualityStatus.Available, true, null, false)]
    [InlineData(QualityStatus.Hold, false, ApprovalSubjectType.HoldRelease, true)]
    [InlineData(QualityStatus.Recalled, false, null, true)]
    [InlineData(QualityStatus.Damaged, false, null, true)]
    public void Quality_transitions_follow_the_table(QualityStatus status, bool holdable, ApprovalSubjectType? release, bool dispositionable)
    {
        Assert.Equal(holdable, QualityRules.Holdable(status));
        Assert.Equal(release, QualityRules.ReleaseSubject(status));
        Assert.Equal(dispositionable, QualityRules.Dispositionable(status));
    }

    [Fact]
    [Trait("Requirement", "STATE-13")]
    public void Every_quality_status_is_in_the_table()
    {
        Assert.Equal(5, Enum.GetValues<QualityStatus>().Length); // a new status must be added to the table above
    }
}
