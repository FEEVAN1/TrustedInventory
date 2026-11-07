using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Npgsql.NameTranslation;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Alerts;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Organization;
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
        builder.MapEnum<TenantStatus>("ti.tenant_status", Verbatim);
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
    }

    public static void Map(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder o)
    {
        o.MapEnum<TenantStatus>("tenant_status", AppDbContext.Schema, Verbatim);
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
    }
}
