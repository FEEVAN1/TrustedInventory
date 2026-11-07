using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TrustedInventory.Application.Audit;

namespace TrustedInventory.Infrastructure.Audit;

/// <summary>Read-only mapping of ti.audit_event; inserts go through <see cref="AuditAppender"/> only.</summary>
internal sealed class StoredAuditEventConfiguration : IEntityTypeConfiguration<StoredAuditEvent>
{
    public void Configure(EntityTypeBuilder<StoredAuditEvent> builder)
    {
        builder.ToTable("audit_event");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.BeforeState).HasColumnType("jsonb");
        builder.Property(e => e.AfterState).HasColumnType("jsonb");
    }
}
