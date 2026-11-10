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

internal sealed class OutboundOrderConfiguration() : TenantEntityConfiguration<Domain.Outbound.OutboundOrder>("outbound_order")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Domain.Outbound.OutboundOrder> builder)
    {
        builder.HasOne<Partner>().WithMany().HasForeignKey(o => o.CustomerId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Warehouse>().WithMany().HasForeignKey(o => o.WarehouseId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class OutboundOrderLineConfiguration() : TenantEntityConfiguration<Domain.Outbound.OutboundOrderLine>("outbound_order_line")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Domain.Outbound.OutboundOrderLine> builder)
    {
        builder.HasOne<Domain.Outbound.OutboundOrder>().WithMany().HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Product>().WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class ReservationConfiguration() : TenantEntityConfiguration<Domain.Outbound.Reservation>("reservation")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Domain.Outbound.Reservation> builder) =>
        builder.HasOne<Domain.Outbound.OutboundOrderLine>().WithMany().HasForeignKey(r => r.OrderLineId).OnDelete(DeleteBehavior.NoAction);
}

internal sealed class PickConfiguration() : TenantEntityConfiguration<Domain.Outbound.Pick>("pick")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Domain.Outbound.Pick> builder) =>
        builder.HasOne<Domain.Outbound.Reservation>().WithMany().HasForeignKey(p => p.ReservationId).OnDelete(DeleteBehavior.NoAction);
}

/// <summary>Append-only (0010 trigger): no version or update columns, so those inherited members are not mapped (DEC-055).</summary>
internal sealed class ShipmentConfiguration() : TenantEntityConfiguration<Domain.Outbound.Shipment>("shipment")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Domain.Outbound.Shipment> builder)
    {
        builder.Ignore(x => x.Version);
        builder.Ignore(x => x.UpdatedAt);
        builder.HasOne<Domain.Outbound.OutboundOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class ShipmentLineConfiguration() : TenantEntityConfiguration<Domain.Outbound.ShipmentLine>("shipment_line")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Domain.Outbound.ShipmentLine> builder)
    {
        builder.HasOne<Domain.Outbound.Shipment>().WithMany().HasForeignKey(x => x.ShipmentId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<StockMovement>().WithMany().HasForeignKey(x => x.MovementId).OnDelete(DeleteBehavior.NoAction);
    }
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

internal sealed class ApprovalConfiguration() : TenantEntityConfiguration<Domain.Operations.Approval>("approval")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Domain.Operations.Approval> builder) { }
}

internal sealed class AdjustmentConfiguration() : TenantEntityConfiguration<Domain.Operations.Adjustment>("adjustment")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Domain.Operations.Adjustment> builder)
    {
        builder.Property(a => a.DeltaQty).HasColumnName("delta_qty");
        builder.HasOne<Domain.Operations.Approval>().WithMany().HasForeignKey(a => a.ApprovalId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<StockMovement>().WithMany().HasForeignKey(a => a.PostedMovementId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class TransferConfiguration() : TenantEntityConfiguration<Domain.Operations.Transfer>("transfer")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Domain.Operations.Transfer> builder) { }
}

internal sealed class TransferLineConfiguration() : TenantEntityConfiguration<Domain.Operations.TransferLine>("transfer_line")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Domain.Operations.TransferLine> builder) =>
        builder.HasOne<Domain.Operations.Transfer>().WithMany().HasForeignKey(l => l.TransferId).OnDelete(DeleteBehavior.NoAction);
}

internal sealed class StockCountConfiguration() : TenantEntityConfiguration<Domain.Operations.StockCount>("stock_count")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Domain.Operations.StockCount> builder) { }
}

internal sealed class StockCountBinConfiguration() : TenantEntityConfiguration<Domain.Operations.StockCountBin>("stock_count_bin")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Domain.Operations.StockCountBin> builder) =>
        builder.HasOne<Domain.Operations.StockCount>().WithMany().HasForeignKey(b => b.CountId).OnDelete(DeleteBehavior.NoAction);
}

/// <summary>variance_qty is a generated column; the domain computes the same value, so it is not mapped.</summary>
internal sealed class StockCountLineConfiguration() : TenantEntityConfiguration<Domain.Operations.StockCountLine>("stock_count_line")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Domain.Operations.StockCountLine> builder) =>
        builder.HasOne<Domain.Operations.StockCount>().WithMany().HasForeignKey(l => l.CountId).OnDelete(DeleteBehavior.NoAction);
}

internal sealed class CustomerReturnConfiguration() : TenantEntityConfiguration<Domain.Operations.CustomerReturn>("customer_return")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Domain.Operations.CustomerReturn> builder) =>
        builder.HasOne<StockMovement>().WithMany().HasForeignKey(r => r.ReceiptMovementId).OnDelete(DeleteBehavior.NoAction);
}

internal sealed class AttachmentConfiguration() : TenantEntityConfiguration<Domain.Operations.Attachment>("attachment")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Domain.Operations.Attachment> builder) =>
        builder.Property(a => a.DigestSha256).HasColumnName("digest_sha256");
}

internal sealed class ExportJobConfiguration() : TenantEntityConfiguration<Domain.Operations.ExportJob>("export_job")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Domain.Operations.ExportJob> builder)
    {
        builder.Property(j => j.Filter).HasColumnType("jsonb");
        builder.Property(j => j.Manifest).HasColumnType("jsonb");
    }
}
