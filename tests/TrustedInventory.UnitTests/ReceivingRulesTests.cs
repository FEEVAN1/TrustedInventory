using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Locations;

namespace TrustedInventory.UnitTests;

/// <summary>Storage compatibility (DEC-012) and initial quality of received stock (INB-003/004, DEC-048).</summary>
public class ReceivingRulesTests
{
    private static readonly string[] None = [];

    [Theory]
    [Trait("Requirement", "STORE-COMPAT")]
    [InlineData(null, null, null, null, true)]   // no profile: any zone
    [InlineData(2.0, 8.0, 2.0, 8.0, true)]       // zone range equals the profile
    [InlineData(3.0, 6.0, 2.0, 8.0, true)]       // zone range inside the profile
    [InlineData(0.0, 8.0, 2.0, 8.0, false)]      // zone can be colder than allowed
    [InlineData(2.0, 10.0, 2.0, 8.0, false)]     // zone can be warmer than allowed
    [InlineData(null, null, 2.0, 8.0, false)]    // uncontrolled ambient zone never satisfies a profile
    public void Temperature_compatibility_requires_the_zone_range_inside_the_profile(double? zmin, double? zmax, double? pmin, double? pmax, bool expected) =>
        Assert.Equal(expected, StorageCompatibility.IsCompatible((decimal?)zmin, (decimal?)zmax, (decimal?)pmin, (decimal?)pmax, null, None));

    [Fact]
    [Trait("Requirement", "STORE-COMPAT")]
    public void Hazard_classes_need_an_explicit_allowance()
    {
        Assert.True(StorageCompatibility.IsCompatible(null, null, null, null, null, ["UN1993"]));
        Assert.False(StorageCompatibility.IsCompatible(null, null, null, null, "UN1993", None));
        Assert.True(StorageCompatibility.IsCompatible(null, null, null, null, "UN1993", ["UN1993"]));
        Assert.False(StorageCompatibility.IsCompatible(2, 8, 2, 8, "UN1993", ["UN3480"]));
    }

    [Theory]
    [Trait("Requirement", "INB-004")]
    [Trait("Requirement", "INB-003")]
    [InlineData(HandlingProfile.Medical, InspectionResult.Passed, false, QualityStatus.Quarantine)]
    [InlineData(HandlingProfile.Medical, InspectionResult.Failed, true, QualityStatus.Quarantine)]
    [InlineData(HandlingProfile.General, InspectionResult.Passed, false, QualityStatus.Available)]
    [InlineData(HandlingProfile.General, InspectionResult.Failed, false, QualityStatus.Hold)]
    [InlineData(HandlingProfile.General, InspectionResult.Passed, true, QualityStatus.Hold)]
    public void Initial_quality_is_never_available_for_medical_failed_or_exceptional_arrivals(
        HandlingProfile profile, InspectionResult inspection, bool exceptional, QualityStatus expected) =>
        Assert.Equal(expected, ReceivingRules.InitialQuality(profile, inspection, exceptional));

    [Fact]
    [Trait("Requirement", "INB-003")]
    public void Expiry_and_temperature_exceptions_use_the_warehouse_date_and_the_profile_range()
    {
        var today = new DateOnly(2026, 11, 8);
        Assert.True(ReceivingRules.IsExpired(new DateOnly(2026, 11, 7), today));
        Assert.False(ReceivingRules.IsExpired(today, today));   // usable through its expiry date at receipt
        Assert.False(ReceivingRules.IsExpired(null, today));
        Assert.True(ReceivingRules.IsOutOfRange(8.01m, 2, 8));
        Assert.True(ReceivingRules.IsOutOfRange(1.99m, 2, 8));
        Assert.False(ReceivingRules.IsOutOfRange(8m, 2, 8));
        Assert.False(ReceivingRules.IsOutOfRange(null, 2, 8));     // not measured: no exception (medical evidence rules decide)
        Assert.False(ReceivingRules.IsOutOfRange(30m, null, null)); // no profile limits
        Assert.True(ReceivingRules.NeedsReason(InspectionResult.Failed, false));
        Assert.True(ReceivingRules.NeedsReason(InspectionResult.Passed, true));
        Assert.False(ReceivingRules.NeedsReason(InspectionResult.Passed, false));
    }
}
