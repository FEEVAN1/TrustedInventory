using TrustedInventory.Application.Billing;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.UnitTests;

/// <summary>Checkout preview wording and consent digest (BIL-003, BIL-001, DEC-063).</summary>
public sealed class CheckoutTermsTests
{
    private static readonly Guid Tenant = Guid.Parse("018f0000-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private static CheckoutPriceRow Price(long amount = 2500, int exponent = 2, BillingInterval interval = BillingInterval.Month, Guid? id = null) =>
        new(id ?? Guid.Parse("018f0000-0000-7000-8000-0000000000aa"), Guid.Parse("018f0000-0000-7000-8000-0000000000bb"), "growth", true, true, "XTS",
            amount, exponent, interval, Now.AddDays(-1), null, null, null);

    [Theory]
    [Trait("Requirement", "BIL-001")]
    [InlineData(2500L, 2, "XTS 25.00")]
    [InlineData(5L, 2, "XTS 0.05")]
    [InlineData(1234567L, 3, "XTS 1234.567")]
    [InlineData(42L, 0, "XTS 42")]
    [InlineData(100000L, 4, "XTS 10.0000")]
    public void Amounts_are_shown_from_minor_units_without_floating_point(long minor, int exponent, string expected) =>
        Assert.Equal(expected, CheckoutService.Format(Price(minor, exponent)));

    [Fact]
    [Trait("Requirement", "BIL-003")]
    public void The_digest_covers_the_terms_but_not_the_time()
    {
        var a = CheckoutService.Preview(Tenant, Price(), Now);
        Assert.Equal(a.PreviewDigest, CheckoutService.Preview(Tenant, Price(), Now.AddHours(5)).PreviewDigest);
        Assert.Matches("^[0-9a-f]{64}$", a.PreviewDigest);
        Assert.NotEqual(a.PreviewDigest, CheckoutService.Preview(Tenant, Price(amount: 2501), Now).PreviewDigest);
        Assert.NotEqual(a.PreviewDigest, CheckoutService.Preview(Tenant, Price(interval: BillingInterval.Year), Now).PreviewDigest);
        Assert.NotEqual(a.PreviewDigest, CheckoutService.Preview(Tenant, Price(id: Guid.NewGuid()), Now).PreviewDigest);
        Assert.NotEqual(a.PreviewDigest, CheckoutService.Preview(Guid.NewGuid(), Price(), Now).PreviewDigest);
        Assert.Contains("Renews every month at XTS 25.00", a.Renewal);
        Assert.Contains("No automatic refunds and no proration", a.CancellationPolicy);
    }

    [Fact]
    [Trait("Requirement", "BIL-002")]
    public void An_open_session_reads_as_expired_from_its_expiry_instant()
    {
        Assert.Equal(CheckoutStatus.Open, CheckoutService.Status(CheckoutStatus.Open, Now.AddTicks(1), Now));
        Assert.Equal(CheckoutStatus.Expired, CheckoutService.Status(CheckoutStatus.Open, Now, Now));
        Assert.Equal(CheckoutStatus.Completed, CheckoutService.Status(CheckoutStatus.Completed, Now.AddDays(-1), Now));
    }
}
