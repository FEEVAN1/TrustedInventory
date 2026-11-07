namespace TrustedInventory.Domain.Catalog;

// Values match the PostgreSQL enum labels (docs/enums.md); never reorder or rename.

public enum HandlingProfile
{
    General,
    Medical,
}

public enum TrackingMode
{
    Quantity,
    Lot,
    Serial,
    LotAndSerial,
}

public enum RotationRule
{
    FIFO,
    LIFO,
    FEFO,
}

public enum PartnerType
{
    Supplier,
    Customer,
    Both,
}
