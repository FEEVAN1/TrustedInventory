using TrustedInventory.Domain.Saas;

namespace TrustedInventory.UnitTests;

/// <summary>How verified events move a subscription (BIL-005, BIL-007, DEC-064).</summary>
public class BillingEventRulesTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 31, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextEnd = new(2026, 3, 31, 0, 0, 0, TimeSpan.Zero);

    private static SubscriptionFacts Active(long revision = 5) => new(SubscriptionState.Active, "sim_sub_1", revision, Start, End, null);

    [Fact]
    [Trait("Requirement", "BIL-007")]
    public void A_failed_renewal_starts_one_grace_period_from_the_due_date_and_retries_do_not_extend_it()
    {
        var pastDue = BillingEventRules.RenewalFailed(Active(), 6, End)!;
        Assert.Equal(SubscriptionState.PastDue, pastDue.State);
        Assert.Equal(End.AddDays(7), pastDue.GraceEnd);

        var retried = BillingEventRules.RenewalFailed(pastDue, 7, End.AddDays(3))!;
        Assert.Equal(SubscriptionState.PastDue, retried.State);
        Assert.Equal(End.AddDays(7), retried.GraceEnd);
        Assert.Equal(7, retried.ProviderRevision);
    }

    [Fact]
    [Trait("Requirement", "BIL-005")]
    [Trait("Requirement", "AT-BIL-02")]
    public void An_older_failure_after_a_newer_payment_changes_nothing()
    {
        var paid = BillingEventRules.RenewalPaid(Active(), 8, End, NextEnd, otherDuesOpen: false)!;
        Assert.Equal(NextEnd, paid.PeriodEnd);
        Assert.Null(BillingEventRules.RenewalFailed(paid, 7, End));
        Assert.Null(BillingEventRules.RenewalFailed(paid, 8, End)); // the same revision is not newer either
        Assert.Null(BillingEventRules.RenewalPaid(paid, 6, Start, End, otherDuesOpen: false));
    }

    [Fact]
    [Trait("Requirement", "BIL-007")]
    public void A_payment_clears_past_due_only_when_no_other_dues_remain()
    {
        var pastDue = BillingEventRules.RenewalFailed(Active(), 6, End)!;
        var stillOwing = BillingEventRules.RenewalPaid(pastDue, 7, End, NextEnd, otherDuesOpen: true)!;
        Assert.Equal(SubscriptionState.PastDue, stillOwing.State);
        Assert.NotNull(stillOwing.GraceEnd);

        var settled = BillingEventRules.RenewalPaid(stillOwing, 8, End, NextEnd, otherDuesOpen: false)!;
        Assert.Equal(SubscriptionState.Active, settled.State);
        Assert.Null(settled.GraceEnd);
        // A period that is not later than the one held does not move it back.
        Assert.Equal(NextEnd, BillingEventRules.RenewalPaid(settled, 9, Start, End, otherDuesOpen: false)!.PeriodEnd);
    }

    [Fact]
    [Trait("Requirement", "BIL-003")]
    public void Renewal_events_do_not_touch_a_trial_and_a_payment_activates_only_once()
    {
        var trial = new SubscriptionFacts(SubscriptionState.Trialing, null, null, null, null, null);
        Assert.Null(BillingEventRules.RenewalFailed(trial, 1, End));
        Assert.Null(BillingEventRules.RenewalPaid(trial, 1, Start, End, otherDuesOpen: false));

        Assert.Equal(ActivationOutcome.Activate, BillingEventRules.Activation(trial, "sim_sub_1"));
        Assert.Equal(ActivationOutcome.AlreadyActive, BillingEventRules.Activation(Active(), "sim_sub_1"));
        Assert.Equal(ActivationOutcome.Conflict, BillingEventRules.Activation(Active(), "sim_sub_2"));
        var ended = new SubscriptionFacts(SubscriptionState.Cancelled, "sim_sub_1", 9, Start, End, null);
        Assert.Equal(ActivationOutcome.Activate, BillingEventRules.Activation(ended, "sim_sub_2"));
    }

    [Theory]
    [Trait("Requirement", "BIL-005")]
    [InlineData(null, null, 1L, true)]
    [InlineData(InvoiceStatus.Open, 3L, 4L, true)]
    [InlineData(InvoiceStatus.Open, 4L, 3L, false)]
    [InlineData(InvoiceStatus.Paid, 3L, 9L, false)]
    public void A_paid_invoice_never_goes_back(InvoiceStatus? current, long? currentRevision, long incoming, bool changes) =>
        Assert.Equal(changes, BillingEventRules.InvoiceChanges(current, currentRevision, incoming));
}
