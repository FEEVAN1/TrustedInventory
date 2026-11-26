using TrustedInventory.Application.Organization;

namespace TrustedInventory.UnitTests;

public sealed class PlatformAlertRulesTests
{
    private static PlatformHealthDto Healthy() => new(DateTimeOffset.UnixEpoch, 10, 10, 0, 2.0, 5.0, 3, 0, 0, null, 0, 0, 0, 0, 0, 0);

    [Fact]
    [Trait("Requirement", "SOP-003")]
    public void A_healthy_platform_raises_no_alert() =>
        Assert.DoesNotContain(PlatformMonitorService.Evaluate(Healthy()), s => s.Present);

    [Theory]
    [InlineData(299, false)]
    [InlineData(300, false)]
    [InlineData(301, true)]
    [Trait("Requirement", "SOP-003")]
    public void Webhook_backlog_alerts_only_above_five_minutes(double ageSeconds, bool alert)
    {
        var signal = PlatformMonitorService.Evaluate(Healthy() with { WebhookBacklog = 1, OldestWebhookAgeSeconds = ageSeconds })
            .Single(s => s.Kind == PlatformAlertKind.WebhookBacklog);
        Assert.Equal(alert, signal.Present);
        Assert.Equal("Critical", signal.Severity);
    }

    [Fact]
    [Trait("Requirement", "SOP-003")]
    public void Any_entitlement_mismatch_is_critical_and_counter_divergence_is_a_reconciliation_warning()
    {
        var signals = PlatformMonitorService.Evaluate(Healthy() with { EntitlementMismatchTasks = 1, SeatCounterDivergences = 2 });
        Assert.True(signals.Single(s => s.Kind == PlatformAlertKind.EntitlementMismatch) is { Present: true, Severity: "Critical" });
        var reconciliation = signals.Single(s => s.Kind == PlatformAlertKind.ReconciliationBacklog);
        Assert.True(reconciliation is { Present: true, Severity: "Warning" });
        Assert.Equal(2, reconciliation.Detail["seatCounterDivergences"]);
    }
}
