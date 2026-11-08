using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Infrastructure.Persistence.Configurations;

namespace TrustedInventory.Infrastructure.Organization;

internal sealed class InvitationConfiguration() : TenantEntityConfiguration<Invitation>("invitation")
{
    protected override void ConfigureEntity(EntityTypeBuilder<Invitation> builder)
    {
        builder.Property(i => i.Roles).HasColumnName("roles");
        builder.Property(i => i.WarehouseIds).HasColumnName("warehouse_ids");
        builder.HasOne<Membership>().WithMany().HasForeignKey(i => i.MembershipId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class OwnershipTransferConfiguration() : TenantEntityConfiguration<OwnershipTransfer>("ownership_transfer")
{
    protected override void ConfigureEntity(EntityTypeBuilder<OwnershipTransfer> builder) =>
        builder.HasOne<Membership>().WithMany().HasForeignKey(t => t.ToMembershipId).OnDelete(DeleteBehavior.NoAction);
}
