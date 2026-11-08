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

internal sealed class ReceiptConfiguration() : TenantEntityConfiguration<Receipt>("receipt")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Receipt> builder) =>
        builder.HasOne<PurchaseOrder>().WithMany().HasForeignKey(r => r.PurchaseOrderId).OnDelete(DeleteBehavior.NoAction);
}

/// <summary>Append-only rows without version columns: inserted once, never updated (0006 triggers).</summary>
internal sealed class ReceiptLineConfiguration : IEntityTypeConfiguration<ReceiptLine>
{
    public void Configure(EntityTypeBuilder<ReceiptLine> builder)
    {
        builder.ToTable("receipt_line");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.HasOne<Receipt>().WithMany().HasForeignKey(l => l.ReceiptId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class ReceiptLineSerialConfiguration : IEntityTypeConfiguration<ReceiptLineSerial>
{
    public void Configure(EntityTypeBuilder<ReceiptLineSerial> builder)
    {
        builder.ToTable("receipt_line_serial");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.HasOne<ReceiptLine>().WithMany().HasForeignKey(s => s.ReceiptLineId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Serial>().WithMany().HasForeignKey(s => s.SerialId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class SerialConfiguration : IEntityTypeConfiguration<Serial>
{
    public void Configure(EntityTypeBuilder<Serial> builder)
    {
        builder.ToTable("serial");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
    }
}

internal sealed class StockBucketConfiguration() : TenantEntityConfiguration<StockBucket>("stock_bucket")
{
    protected override void ConfigureEntity(EntityTypeBuilder<StockBucket> builder) { }
}

internal sealed class WarehouseTaskConfiguration() : TenantEntityConfiguration<WarehouseTask>("warehouse_task")
{
    protected override void ConfigureEntity(EntityTypeBuilder<WarehouseTask> builder) =>
        builder.HasOne<ReceiptLine>().WithMany().HasForeignKey(t => t.ReceiptLineId).OnDelete(DeleteBehavior.NoAction);
}

internal sealed class LotConfiguration() : TenantEntityConfiguration<Lot>("lot")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Lot> builder) { }
}

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable("stock_movement");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
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
