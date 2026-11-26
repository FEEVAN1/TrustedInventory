using TrustedInventory.Domain.Common;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.UnitTests;

public sealed class ClosureRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("acme-ltd", "CLOSE acme-ltd", true)]
    [InlineData("acme-ltd", "close acme-ltd", false)]
    [InlineData("acme-ltd", "CLOSE acme-ltd ", false)]
    [InlineData("acme-ltd", "CLOSE other", false)]
    [InlineData("acme-ltd", null, false)]
    [InlineData("", "CLOSE ", false)]
    [Trait("Requirement", "LFC-002")]
    public void The_confirmation_must_name_the_workspace_exactly(string slug, string? typed, bool accepted) =>
        Assert.Equal(accepted, ClosureRules.ConfirmationMatches(slug, typed));

    [Fact]
    [Trait("Requirement", "LFC-002")]
    public void The_export_window_runs_from_the_end_of_paid_service_never_from_an_earlier_date()
    {
        Assert.Equal(Now.AddDays(90 + 10), ClosureRules.ExportWindowEnds(Now, Now.AddDays(10), 90));
        Assert.Equal(Now.AddDays(90), ClosureRules.ExportWindowEnds(Now, Now.AddDays(-3), 90)); // already ended: from now
        Assert.Equal(Now.AddDays(90), ClosureRules.ExportWindowEnds(Now, null, 90));
        Assert.Throws<DomainException>(() => ClosureRules.ExportWindowEnds(Now, null, 0));
    }

    [Theory]
    [InlineData(TenantLifecycle.Ready, true)]
    [InlineData(TenantLifecycle.Provisioning, false)]
    [InlineData(TenantLifecycle.Archived, false)]
    [InlineData(TenantLifecycle.DeletionPending, false)]
    [InlineData(TenantLifecycle.Deleted, false)]
    [Trait("Requirement", "LFC-002")]
    public void Only_a_ready_workspace_can_be_closed(TenantLifecycle lifecycle, bool closable)
    {
        var refused = Record.Exception(() => ClosureRules.EnsureClosable(lifecycle));
        Assert.Equal(closable, refused is null);
    }

    [Fact]
    [Trait("Requirement", "LFC-002")]
    public void Open_statuses_are_the_ones_the_database_keeps_unique_per_tenant() =>
        Assert.Equal([ClosureStatus.Requested, ClosureStatus.ExportWindow, ClosureStatus.Blocked, ClosureStatus.DeletionPending],
            Enum.GetValues<ClosureStatus>().Where(ClosureRules.IsOpen));
}
