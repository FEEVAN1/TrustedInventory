using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Organization;

namespace TrustedInventory.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenant");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Version).IsConcurrencyToken();
    }
}

internal sealed class WarehouseConfiguration() : TenantEntityConfiguration<Warehouse>("warehouse")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Warehouse> builder) =>
        builder.Property(w => w.TimeZone).HasColumnName("timezone");
}

internal sealed class ZoneConfiguration() : TenantEntityConfiguration<Zone>("zone")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Zone> builder)
    {
        builder.Property(z => z.TempMinC).HasColumnName("temp_min_c");
        builder.Property(z => z.TempMaxC).HasColumnName("temp_max_c");
        // Relationships are declared so EF orders inserts (parents first). The database enforces the
        // tenant-composite foreign keys (TEN-001); EF only needs the single-column shape here.
        builder.HasOne<Warehouse>().WithMany().HasForeignKey(z => z.WarehouseId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class ZoneAllowedHazardClassConfiguration : IEntityTypeConfiguration<ZoneAllowedHazardClass>
{
    public void Configure(EntityTypeBuilder<ZoneAllowedHazardClass> builder)
    {
        builder.ToTable("zone_allowed_hazard_class");
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).ValueGeneratedNever();
        builder.HasOne<Zone>().WithMany().HasForeignKey(h => h.ZoneId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class BinConfiguration() : TenantEntityConfiguration<Bin>("bin")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Bin> builder)
    {
        builder.HasOne<Warehouse>().WithMany().HasForeignKey(b => b.WarehouseId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Zone>().WithMany().HasForeignKey(b => b.ZoneId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Unit>().WithMany().HasForeignKey(b => b.CapacityUomId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class UnitConfiguration() : TenantEntityConfiguration<Unit>("unit")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Unit> builder) { }
}
