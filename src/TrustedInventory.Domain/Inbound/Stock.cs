using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Common;

namespace TrustedInventory.Domain.Inbound;

/// <summary>
/// Stock bucket: a transactional projection of the movement ledger (STK-004). Quantities change only inside stock
/// commands through explicit, locked SQL (STK-005); this entity is read-only in code.
/// </summary>
public sealed class StockBucket : TenantEntity
{
    private StockBucket() { }

    public Guid WarehouseId { get; private set; }
    public Guid BinId { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid? LotId { get; private set; }
    public Guid? SerialId { get; private set; }
    public QualityStatus QualityStatus { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public decimal OnHandQty { get; private set; }
    public decimal ReservedQty { get; private set; }
}

/// <summary>A product lot; medical lots always carry an expiry (0006 CHECK).</summary>
public sealed class Lot : TenantEntity
{
    private Lot() { }

    public Guid ProductId { get; private set; }
    public HandlingProfile HandlingProfile { get; private set; }
    public string LotNumber { get; private set; } = "";
    public DateOnly? ExpiryDate { get; private set; }
    public string? SupplierRef { get; private set; }
}
