using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Npgsql.NameTranslation;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Alerts;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Infrastructure.Persistence;

/// <summary>
/// EF Core mapping of schema <c>ti</c>. Tables come from SQL migrations (db/migrations); EF never creates
/// schema. Entity configurations live next to their module and are discovered from this assembly.
/// </summary>
public sealed partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public const string Schema = "ti";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Column names follow the SQL convention unless a configuration set one explicitly.
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                if (property.GetColumnName() == property.Name)
                    property.SetColumnName(SnakeCase(property.Name));
    }

    public static string SnakeCase(string name) => UpperRun().Replace(name, "$1_$2").ToLowerInvariant();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex UpperRun();
}

/// <summary>
/// PostgreSQL enum types mapped to CLR enums. Labels are used verbatim (PascalCase), matching docs/enums.md.
/// Add each new mapped enum here; both the ADO data source and the EF model use this list.
/// </summary>
public static class PgEnums
{
    private static readonly INpgsqlNameTranslator Verbatim = new NpgsqlNullNameTranslator();

    public static void Map(NpgsqlDataSourceBuilder builder)
    {
        // ti.tenant_status was dropped by 0005 (DEC-042); TenantStatus survives only as a C# value derived from the
        // security suspension, so it has no database mapping.
        builder.MapEnum<RoleName>("ti.role_name", Verbatim);
        builder.MapEnum<HandlingProfile>("ti.handling_profile", Verbatim);
        builder.MapEnum<TrackingMode>("ti.tracking_mode", Verbatim);
        builder.MapEnum<RotationRule>("ti.rotation_rule", Verbatim);
        builder.MapEnum<PartnerType>("ti.partner_type", Verbatim);
        builder.MapEnum<BinKind>("ti.bin_kind", Verbatim);
        builder.MapEnum<AlertType>("ti.alert_type", Verbatim);
        builder.MapEnum<AlertSeverity>("ti.alert_severity", Verbatim);
        builder.MapEnum<AlertStatus>("ti.alert_status", Verbatim);
        builder.MapEnum<ActorType>("ti.actor_type", Verbatim);
        builder.MapEnum<TenantLifecycle>("ti.tenant_lifecycle", Verbatim);
        builder.MapEnum<InvitationStatus>("ti.invitation_status", Verbatim);
        builder.MapEnum<OwnershipTransferStatus>("ti.ownership_transfer_status", Verbatim);
        builder.MapEnum<PurchaseOrderStatus>("ti.purchase_order_status", Verbatim);
        builder.MapEnum<QualityStatus>("ti.quality_status", Verbatim);
        builder.MapEnum<InspectionResult>("ti.inspection_result", Verbatim);
        builder.MapEnum<ReceiptStatus>("ti.receipt_status", Verbatim);
        builder.MapEnum<MovementType>("ti.movement_type", Verbatim);
        builder.MapEnum<DocumentType>("ti.document_type", Verbatim);
        builder.MapEnum<TaskType>("ti.task_type", Verbatim);
        builder.MapEnum<Domain.Inbound.TaskStatus>("ti.task_status", Verbatim);
        builder.MapEnum<Domain.Outbound.OutboundOrderStatus>("ti.outbound_order_status", Verbatim);
        builder.MapEnum<Domain.Outbound.ReservationState>("ti.reservation_state", Verbatim);
        builder.MapEnum<Domain.Operations.ApprovalSubjectType>("ti.approval_subject_type", Verbatim);
        builder.MapEnum<Domain.Operations.ApprovalAction>("ti.approval_action", Verbatim);
        builder.MapEnum<Domain.Operations.ApprovalStatus>("ti.approval_status", Verbatim);
        builder.MapEnum<Domain.Operations.SignatureMeaning>("ti.signature_meaning", Verbatim);
        builder.MapEnum<Domain.Operations.AdjustmentStatus>("ti.adjustment_status", Verbatim);
        builder.MapEnum<Domain.Operations.TransferStatus>("ti.transfer_status", Verbatim);
    }

    public static void Map(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder o)
    {
        o.MapEnum<RoleName>("role_name", AppDbContext.Schema, Verbatim);
        o.MapEnum<HandlingProfile>("handling_profile", AppDbContext.Schema, Verbatim);
        o.MapEnum<TrackingMode>("tracking_mode", AppDbContext.Schema, Verbatim);
        o.MapEnum<RotationRule>("rotation_rule", AppDbContext.Schema, Verbatim);
        o.MapEnum<PartnerType>("partner_type", AppDbContext.Schema, Verbatim);
        o.MapEnum<BinKind>("bin_kind", AppDbContext.Schema, Verbatim);
        o.MapEnum<AlertType>("alert_type", AppDbContext.Schema, Verbatim);
        o.MapEnum<AlertSeverity>("alert_severity", AppDbContext.Schema, Verbatim);
        o.MapEnum<AlertStatus>("alert_status", AppDbContext.Schema, Verbatim);
        o.MapEnum<ActorType>("actor_type", AppDbContext.Schema, Verbatim);
        o.MapEnum<TenantLifecycle>("tenant_lifecycle", AppDbContext.Schema, Verbatim);
        o.MapEnum<InvitationStatus>("invitation_status", AppDbContext.Schema, Verbatim);
        o.MapEnum<OwnershipTransferStatus>("ownership_transfer_status", AppDbContext.Schema, Verbatim);
        o.MapEnum<PurchaseOrderStatus>("purchase_order_status", AppDbContext.Schema, Verbatim);
        o.MapEnum<QualityStatus>("quality_status", AppDbContext.Schema, Verbatim);
        o.MapEnum<InspectionResult>("inspection_result", AppDbContext.Schema, Verbatim);
        o.MapEnum<ReceiptStatus>("receipt_status", AppDbContext.Schema, Verbatim);
        o.MapEnum<MovementType>("movement_type", AppDbContext.Schema, Verbatim);
        o.MapEnum<DocumentType>("document_type", AppDbContext.Schema, Verbatim);
        o.MapEnum<TaskType>("task_type", AppDbContext.Schema, Verbatim);
        o.MapEnum<Domain.Inbound.TaskStatus>("task_status", AppDbContext.Schema, Verbatim);
        o.MapEnum<Domain.Outbound.OutboundOrderStatus>("outbound_order_status", AppDbContext.Schema, Verbatim);
        o.MapEnum<Domain.Outbound.ReservationState>("reservation_state", AppDbContext.Schema, Verbatim);
        o.MapEnum<Domain.Operations.ApprovalSubjectType>("approval_subject_type", AppDbContext.Schema, Verbatim);
        o.MapEnum<Domain.Operations.ApprovalAction>("approval_action", AppDbContext.Schema, Verbatim);
        o.MapEnum<Domain.Operations.ApprovalStatus>("approval_status", AppDbContext.Schema, Verbatim);
        o.MapEnum<Domain.Operations.SignatureMeaning>("signature_meaning", AppDbContext.Schema, Verbatim);
        o.MapEnum<Domain.Operations.AdjustmentStatus>("adjustment_status", AppDbContext.Schema, Verbatim);
        o.MapEnum<Domain.Operations.TransferStatus>("transfer_status", AppDbContext.Schema, Verbatim);
    }
}
