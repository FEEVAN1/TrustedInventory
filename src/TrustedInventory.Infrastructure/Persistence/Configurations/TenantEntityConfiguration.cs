using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TrustedInventory.Domain.Common;

namespace TrustedInventory.Infrastructure.Persistence.Configurations;

/// <summary>Shared mapping for <see cref="TenantEntity"/>: client-generated ids and version as concurrency token.</summary>
public abstract class TenantEntityConfiguration<T>(string table) : IEntityTypeConfiguration<T>
    where T : TenantEntity
{
    public void Configure(EntityTypeBuilder<T> builder)
    {
        builder.ToTable(table);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        // UPDATE ... WHERE version = @original: a concurrent writer surfaces as 412 VERSION_CONFLICT.
        builder.Property(e => e.Version).IsConcurrencyToken();
        ConfigureEntity(builder);
    }

    protected abstract void ConfigureEntity(EntityTypeBuilder<T> builder);
}
