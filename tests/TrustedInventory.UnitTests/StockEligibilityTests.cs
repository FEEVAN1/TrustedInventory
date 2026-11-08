using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Locations;

namespace TrustedInventory.UnitTests;

/// <summary>STK-001 eligibility and allocatable quantity (DEC-049).</summary>
public class StockEligibilityTests
{
    private static readonly DateOnly Today = new(2026, 11, 8);

    private static BucketFacts Eligible(decimal onHand = 10, decimal reserved = 3) =>
        new(QualityStatus.Available, onHand, reserved, new DateOnly(2027, 1, 1), Today, BinKind.Storage, BinActive: true, StorageCompatible: true, Blocked: false);

    [Fact]
    [Trait("Requirement", "STK-001")]
    public void A_released_compatible_unexpired_unblocked_storage_bucket_allocates_on_hand_minus_reserved()
    {
        Assert.Empty(StockEligibility.Reasons(Eligible()));
        Assert.Equal(7m, StockEligibility.Allocatable(Eligible()));
        Assert.Equal(0m, StockEligibility.Allocatable(Eligible(onHand: 5, reserved: 5)));
        Assert.Empty(StockEligibility.Reasons(Eligible() with { LotExpiry = null }));   // untracked stock never expires
        Assert.Empty(StockEligibility.Reasons(Eligible() with { LotExpiry = Today }));  // usable through its expiry date
    }

    public static TheoryData<BucketFacts, IneligibilityReason> SingleFailures() => new()
    {
        { Eligible() with { Quality = QualityStatus.Quarantine }, IneligibilityReason.Quarantine },
        { Eligible() with { Quality = QualityStatus.Hold }, IneligibilityReason.Hold },
        { Eligible() with { Quality = QualityStatus.Recalled }, IneligibilityReason.Recalled },
        { Eligible() with { Quality = QualityStatus.Damaged }, IneligibilityReason.Damaged },
        { Eligible() with { LotExpiry = Today.AddDays(-1) }, IneligibilityReason.Expired },
        { Eligible() with { StorageCompatible = false }, IneligibilityReason.StorageIncompatible },
        { Eligible() with { BinActive = false }, IneligibilityReason.BinInactive },
        { Eligible() with { BinKind = BinKind.Transit }, IneligibilityReason.TransitLocation },
        { Eligible() with { BinKind = BinKind.Staging }, IneligibilityReason.AwaitingPutaway },
        { Eligible() with { BinKind = BinKind.Quarantine }, IneligibilityReason.AwaitingPutaway },
        { Eligible() with { Blocked = true }, IneligibilityReason.Blocked },
    };

    [Theory]
    [MemberData(nameof(SingleFailures))]
    [Trait("Requirement", "STK-001")]
    [Trait("Requirement", "OPS-001")]
    public void Any_single_failure_names_its_reason_and_allocates_nothing(BucketFacts facts, IneligibilityReason reason)
    {
        Assert.Equal([reason], StockEligibility.Reasons(facts));
        Assert.Equal(0m, StockEligibility.Allocatable(facts));
    }

    [Fact]
    [Trait("Requirement", "STK-001")]
    public void Every_failure_is_reported_not_just_the_first()
    {
        var facts = Eligible() with { Quality = QualityStatus.Quarantine, BinKind = BinKind.Staging, LotExpiry = Today.AddDays(-1), Blocked = true };
        Assert.Equal([IneligibilityReason.Quarantine, IneligibilityReason.Expired, IneligibilityReason.AwaitingPutaway, IneligibilityReason.Blocked],
            StockEligibility.Reasons(facts));
    }

    [Fact]
    [Trait("Requirement", "STK-001")]
    public void The_domain_reasons_match_the_contract_enum()
    {
        string[] contract = ["Quarantine", "Hold", "Recalled", "Damaged", "Expired", "StorageIncompatible", "StaleTelemetry", "OpenExcursion",
            "CountLocked", "BinInactive", "TransitLocation", "AwaitingPutaway", "Blocked"]; // docs/openapi.yaml IneligibilityReason
        Assert.Equal(contract, Enum.GetNames<IneligibilityReason>());
    }
}
