using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Locations;

namespace TrustedInventory.Infrastructure.Persistence.Configurations;

internal sealed class PurchaseOrderConfiguration() : TenantEntityConfiguration<PurchaseOrder>("purchase_order")
{
    protected override void ConfigureEntity(EntityTypeBuilder<PurchaseOrder> builder)
    {
        // Single-column shapes so EF orders inserts; the database enforces the tenant-composite keys (TEN-001).
        builder.HasOne<Partner>().WithMany().HasForeignKey(o => o.SupplierId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Warehouse>().WithMany().HasForeignKey(o => o.WarehouseId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class PurchaseOrderLineConfiguration() : TenantEntityConfiguration<PurchaseOrderLine>("purchase_order_line")
{
    protected override void ConfigureEntity(EntityTypeBuilder<PurchaseOrderLine> builder)
    {
        builder.HasOne<PurchaseOrder>().WithMany().HasForeignKey(l => l.PurchaseOrderId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Product>().WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Unit>().WithMany().HasForeignKey(l => l.UomId).OnDelete(DeleteBehavior.NoAction);
    }
}
