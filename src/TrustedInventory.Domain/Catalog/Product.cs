using TrustedInventory.Domain.Common;

namespace TrustedInventory.Domain.Catalog;

/// <summary>
/// Product master record (section 07). Medical products are lot-tracked and FEFO-only; serial-tracked products
/// use whole base units. Handling profile, tracking mode, base unit and precision are fixed after creation
/// because stock history depends on them.
/// </summary>
public sealed class Product : TenantEntity
{
    private Product() { }

    public string Sku { get; private set; } = "";
    public string Name { get; private set; } = "";
    public HandlingProfile HandlingProfile { get; private set; }
    public TrackingMode TrackingMode { get; private set; }
    public Guid BaseUomId { get; private set; }
    public short BasePrecision { get; private set; }
    public RotationRule RotationRule { get; private set; }
    public string? HazardClass { get; private set; }
    public Guid? StorageProfileId { get; private set; }
    public bool Active { get; private set; }

    public bool IsSerialTracked => TrackingMode is TrackingMode.Serial or TrackingMode.LotAndSerial;

    /// <summary>Default rotation: FEFO for medical (the only option), FIFO for general goods (section 03).</summary>
    public static RotationRule DefaultRotation(HandlingProfile profile) =>
        profile == HandlingProfile.Medical ? RotationRule.FEFO : RotationRule.FIFO;

    public static Product Create(Guid tenantId, string sku, string name, HandlingProfile handlingProfile, TrackingMode trackingMode,
        Guid baseUomId, short basePrecision, RotationRule? rotationRule, Guid? storageProfileId, string? hazardClass,
        string actor, DateTimeOffset now)
    {
        if (basePrecision is < 0 or > 6)
            throw new DomainException("basePrecision must be between 0 and 6.");
        var product = new Product
        {
            Sku = sku, Name = name, HandlingProfile = handlingProfile, TrackingMode = trackingMode, BaseUomId = baseUomId,
            BasePrecision = basePrecision, RotationRule = rotationRule ?? DefaultRotation(handlingProfile),
            StorageProfileId = storageProfileId, HazardClass = hazardClass, Active = true,
        };
        product.EnsureRules();
        product.Stamp(tenantId, actor, now);
        return product;
    }

    public void Update(string name, RotationRule rotationRule, Guid? storageProfileId, string? hazardClass, DateTimeOffset now)
    {
        EnsureActive();
        Name = name;
        RotationRule = rotationRule;
        StorageProfileId = storageProfileId;
        HazardClass = hazardClass;
        EnsureRules();
        Touch(now);
    }

    public void Retire(DateTimeOffset now)
    {
        EnsureActive();
        Active = false;
        Touch(now);
    }

    public void EnsureActive()
    {
        if (!Active)
            throw new DomainException("Product is retired.");
    }

    private void EnsureRules()
    {
        if (HandlingProfile == HandlingProfile.Medical && TrackingMode is not (TrackingMode.Lot or TrackingMode.LotAndSerial))
            throw new DomainException("Medical products must be tracked by Lot or LotAndSerial.");
        if (HandlingProfile == HandlingProfile.Medical && RotationRule != RotationRule.FEFO)
            throw new DomainException("Medical products use FEFO rotation only.");
        if (IsSerialTracked && BasePrecision != 0)
            throw new DomainException("Serial-tracked products require basePrecision 0 (whole units).");
    }
}

/// <summary>Alternate unit mapped to the product base unit (section 07). The factor is frozen on transaction lines.</summary>
public sealed class ProductConversion : TenantEntity
{
    private ProductConversion() { }

    public Guid ProductId { get; private set; }
    public Guid AlternateUomId { get; private set; }
    public decimal FactorToBase { get; private set; }
    public bool Active { get; private set; }

    public static ProductConversion Create(Guid tenantId, Product product, Guid alternateUomId, decimal factorToBase,
        string actor, DateTimeOffset now)
    {
        product.EnsureActive();
        if (alternateUomId == product.BaseUomId)
            throw new DomainException("The alternate unit must differ from the product base unit.");
        if (factorToBase <= 0)
            throw new DomainException("factorToBase must be greater than zero.");
        // One alternate unit must convert to a quantity the product can hold (section 07).
        if (decimal.Round(factorToBase, product.BasePrecision) != factorToBase)
            throw new DomainException($"factorToBase has more decimal places than the product base precision ({product.BasePrecision}).");

        var conversion = new ProductConversion
        {
            ProductId = product.Id, AlternateUomId = alternateUomId, FactorToBase = factorToBase, Active = true,
        };
        conversion.Stamp(tenantId, actor, now);
        return conversion;
    }

    public void Retire(DateTimeOffset now)
    {
        if (!Active)
            throw new DomainException("Conversion is retired.");
        Active = false;
        Touch(now);
    }
}

/// <summary>GTIN mapped to one product (INB-002). Stored as GTIN-14 with leading zeros preserved.</summary>
public sealed class ProductBarcode : TenantEntity
{
    private ProductBarcode() { }

    public Guid ProductId { get; private set; }
    public string Gtin { get; private set; } = "";
    public bool Active { get; private set; }

    public static ProductBarcode Create(Guid tenantId, Product product, string gtin14, string actor, DateTimeOffset now)
    {
        product.EnsureActive();
        if (!Catalog.Gtin.IsValidGtin14(gtin14))
            throw new DomainException("GTIN must be 14 digits with a valid check digit.");
        var barcode = new ProductBarcode { ProductId = product.Id, Gtin = gtin14, Active = true };
        barcode.Stamp(tenantId, actor, now);
        return barcode;
    }

    public void Retire(DateTimeOffset now)
    {
        if (!Active)
            throw new DomainException("Barcode is retired.");
        Active = false;
        Touch(now);
    }
}

/// <summary>GS1 GTIN rules (INB-002): GTIN-8/12/13/14 accepted, normalised to 14 digits; leading zeros are significant.</summary>
public static class Gtin
{
    public static bool TryNormalize(string? input, out string gtin14)
    {
        gtin14 = "";
        if (input is null || input.Length is not (8 or 12 or 13 or 14) || !input.All(char.IsAsciiDigit))
            return false;
        var padded = input.PadLeft(14, '0');
        if (!IsValidGtin14(padded))
            return false;
        gtin14 = padded;
        return true;
    }

    /// <summary>GS1 mod-10: weights 3,1,3,… from the digit left of the check digit.</summary>
    public static bool IsValidGtin14(string gtin14)
    {
        if (gtin14.Length != 14 || !gtin14.All(char.IsAsciiDigit))
            return false;
        var sum = 0;
        for (var i = 0; i < 13; i++)
            sum += (gtin14[i] - '0') * (i % 2 == 0 ? 3 : 1);
        return (10 - sum % 10) % 10 == gtin14[13] - '0';
    }
}
