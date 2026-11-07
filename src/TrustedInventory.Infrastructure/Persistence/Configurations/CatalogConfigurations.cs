using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TrustedInventory.Domain.Catalog;

namespace TrustedInventory.Infrastructure.Persistence.Configurations;

// Relationships are declared so EF orders inserts (parents first). The database enforces the
// tenant-composite foreign keys (TEN-001); EF only needs the single-column shape here.

internal sealed class ExcursionPolicyConfiguration() : TenantEntityConfiguration<ExcursionPolicy>("excursion_policy")
{
    // notify_recipients is not mapped yet: inserts take the database default '[]' until M5 adds notifications.
    protected override void ConfigureEntity(EntityTypeBuilder<ExcursionPolicy> builder) { }
}

internal sealed class StorageProfileConfiguration() : TenantEntityConfiguration<StorageProfile>("storage_profile")
{
    protected override void ConfigureEntity(EntityTypeBuilder<StorageProfile> builder) =>
        builder.HasOne<ExcursionPolicy>().WithMany().HasForeignKey(p => p.ExcursionPolicyId).OnDelete(DeleteBehavior.NoAction);
}

internal sealed class ProductConfiguration() : TenantEntityConfiguration<Product>("product")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Product> builder)
    {
        builder.Ignore(p => p.IsSerialTracked);
        builder.HasOne<Unit>().WithMany().HasForeignKey(p => p.BaseUomId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<StorageProfile>().WithMany().HasForeignKey(p => p.StorageProfileId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class ProductConversionConfiguration() : TenantEntityConfiguration<ProductConversion>("product_conversion")
{
    protected override void ConfigureEntity(EntityTypeBuilder<ProductConversion> builder)
    {
        builder.HasOne<Product>().WithMany().HasForeignKey(c => c.ProductId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Unit>().WithMany().HasForeignKey(c => c.AlternateUomId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class ProductBarcodeConfiguration() : TenantEntityConfiguration<ProductBarcode>("product_barcode")
{
    protected override void ConfigureEntity(EntityTypeBuilder<ProductBarcode> builder) =>
        builder.HasOne<Product>().WithMany().HasForeignKey(b => b.ProductId).OnDelete(DeleteBehavior.NoAction);
}

internal sealed class PartnerConfiguration() : TenantEntityConfiguration<Partner>("partner")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Partner> builder) { }
}
