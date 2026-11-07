using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Infrastructure.Persistence.Configurations;

namespace TrustedInventory.Infrastructure.Organization;

internal sealed class MembershipConfiguration() : TenantEntityConfiguration<Membership>("membership")
{
    // Relationships only order inserts; the database enforces the tenant-composite keys (TEN-001).
    protected override void ConfigureEntity(EntityTypeBuilder<Membership> builder) =>
        builder.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.NoAction);
}

internal sealed class RoleGrantConfiguration() : TenantEntityConfiguration<RoleGrant>("role_grant")
{
    protected override void ConfigureEntity(EntityTypeBuilder<RoleGrant> builder)
    {
        builder.Ignore(g => g.Active);
        builder.HasOne<Membership>().WithMany().HasForeignKey(g => g.MembershipId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Warehouse>().WithMany().HasForeignKey(g => g.WarehouseId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class ConfigurationVersionConfiguration : IEntityTypeConfiguration<ConfigurationVersion>
{
    public void Configure(EntityTypeBuilder<ConfigurationVersion> builder)
    {
        builder.ToTable("configuration_version");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.Settings).HasColumnType("jsonb");
    }
}
