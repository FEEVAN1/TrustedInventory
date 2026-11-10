using TrustedInventory.Domain.Common;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.UnitTests;

/// <summary>Integer money and catalog rules (BIL-001, ENT-001, ENT-002, DEC-062).</summary>
public sealed class BillingRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, long?> Limits(long each) =>
        Enum.GetValues<QuotaKey>().ToDictionary(SaasWire.Quota, _ => (long?)each);

    [Fact]
    [Trait("Requirement", "BIL-001")]
    public void Money_is_integer_minor_units_with_an_iso_code_and_exponent()
    {
        var a = Money.Of("XTS", 1050, 2);
        Assert.Equal(1550, a.Plus(Money.Of("XTS", 500, 2)).MinorUnits);
        Assert.Equal(50, a.Minus(Money.Of("XTS", 1000, 2)).MinorUnits);
        Assert.Throws<DomainException>(() => Money.Of("xts", 1, 2));
        Assert.Throws<DomainException>(() => Money.Of("XT", 1, 2));
        Assert.Throws<DomainException>(() => Money.Of("XTS", -1, 2));
        Assert.Throws<DomainException>(() => Money.Of("XTS", 1, 5));
        Assert.Throws<DomainException>(() => Money.Of("XTS", 1, -1));
    }

    [Fact]
    [Trait("Requirement", "BIL-001")]
    public void Money_never_combines_currencies_or_goes_negative()
    {
        var xts = Money.Of("XTS", 100, 2);
        Assert.Throws<DomainException>(() => xts.Plus(Money.Of("USD", 100, 2)));
        Assert.Throws<DomainException>(() => xts.Plus(Money.Of("XTS", 100, 0)));
        Assert.Throws<DomainException>(() => xts.Minus(Money.Of("XTS", 101, 2)));
        Assert.Throws<OverflowException>(() => Money.Of("XTS", long.MaxValue, 2).Plus(Money.Of("XTS", 1, 2)));
    }

    [Fact]
    [Trait("Requirement", "BIL-001")]
    public void While_the_simulator_is_the_provider_only_a_positive_test_currency_price_is_allowed()
    {
        CatalogRules.ValidatePrice(Money.Of("XTS", 1000, 2), simulatorOnly: true);
        Assert.Throws<DomainException>(() => CatalogRules.ValidatePrice(Money.Of("USD", 1000, 2), simulatorOnly: true));
        Assert.Throws<DomainException>(() => CatalogRules.ValidatePrice(Money.Of("XTS", 0, 2), simulatorOnly: true));
        // An approved provider (not yet configured, D09) would allow real currencies.
        CatalogRules.ValidatePrice(Money.Of("USD", 1000, 2), simulatorOnly: false);
    }

    [Fact]
    [Trait("Requirement", "ENT-002")]
    public void A_plan_uses_catalog_keys_with_every_quota_limited()
    {
        CatalogRules.ValidatePlan("growth", "Growth", ["core_inventory", "service_api"], Limits(5), 14);
        Assert.Throws<DomainException>(() => CatalogRules.ValidatePlan("growth", "Growth", [], Limits(5), 14));
        Assert.Throws<DomainException>(() => CatalogRules.ValidatePlan("growth", "Growth", ["core_inventory", "core_inventory"], Limits(5), 14));
        Assert.Throws<DomainException>(() => CatalogRules.ValidatePlan("growth", "Growth", ["flying"], Limits(5), 14));
        Assert.Throws<DomainException>(() => CatalogRules.ValidatePlan("growth", "Growth", ["core_inventory"], Limits(-1), 14));
        var missing = Limits(5);
        missing.Remove("attachment_bytes");
        Assert.Throws<DomainException>(() => CatalogRules.ValidatePlan("growth", "Growth", ["core_inventory"], missing, 14));
        var extra = Limits(5);
        extra["api_calls"] = 5;
        Assert.Throws<DomainException>(() => CatalogRules.ValidatePlan("growth", "Growth", ["core_inventory"], extra, 14));
        var unset = Limits(5);
        unset["active_seats"] = null;
        Assert.Throws<DomainException>(() => CatalogRules.ValidatePlan("growth", "Growth", ["core_inventory"], unset, 14));
        foreach (var code in new[] { "G", "1growth", "Growth", "growth_plan", "g" + new string('x', 40) })
            Assert.Throws<DomainException>(() => CatalogRules.ValidatePlan(code, "Growth", ["core_inventory"], Limits(5), 14));
        Assert.Throws<DomainException>(() => CatalogRules.ValidatePlan("growth", " ", ["core_inventory"], Limits(5), 14));
        Assert.Throws<DomainException>(() => CatalogRules.ValidatePlan("growth", "Growth", ["core_inventory"], Limits(5), 91));
    }

    [Fact]
    [Trait("Requirement", "ENT-001")]
    public void Plan_status_is_draft_until_published_and_retired_when_no_price_remains_active()
    {
        Assert.Equal(PlanVersionStatus.Draft, CatalogRules.Status(null, [(Now, null)], Now));
        Assert.Equal(PlanVersionStatus.Published, CatalogRules.Status(Now, [(Now.AddDays(-1), null)], Now));
        Assert.Equal(PlanVersionStatus.Published, CatalogRules.Status(Now, [(Now.AddDays(1), null)], Now));
        Assert.Equal(PlanVersionStatus.Published, CatalogRules.Status(Now, [(Now.AddDays(-9), Now.AddDays(-1)), (Now.AddDays(-1), Now.AddSeconds(1))], Now));
        Assert.Equal(PlanVersionStatus.Retired, CatalogRules.Status(Now, [(Now.AddDays(-9), Now)], Now));
        Assert.Equal(PlanVersionStatus.Retired, CatalogRules.Status(Now, [], Now));
    }
}
